using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using BCrypt.Net;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly INotificationService _notificationService;
    private readonly IUserPolicyService _userPolicyService;
    private readonly IEmailSender _emailSender;
    private const string CurrentTermsVersion = "terms-v1";
    private const string CurrentPrivacyVersion = "privacy-v1";

    public AuthController(AppDbContext context, IConfiguration configuration, ILogger<AuthController> logger, INotificationService notificationService, IUserPolicyService userPolicyService, IEmailSender emailSender)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
        _notificationService = notificationService;
        _userPolicyService = userPolicyService;
        _emailSender = emailSender;
    }

    [EnableRateLimiting("auth")]
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var firstName = request.FirstName.Trim();
        var lastName = request.LastName.Trim();
        var userType = request.UserType.Trim().ToLowerInvariant();
        var idNumber = request.IdNumber?.Trim();
        var address = request.Address.Trim();
        var now = DateTime.UtcNow;

        if (firstName.Length is < 1 or > 50 || lastName.Length is < 1 or > 50)
            return BadRequest(new AuthResponse { Success = false, Message = "Enter a valid first and last name." });
        if (request.Password.Length < 12 || request.Password.Length > 72 ||
            System.Text.Encoding.UTF8.GetByteCount(request.Password) > 72)
            return BadRequest(new AuthResponse { Success = false, Message = "Use a password between 12 and 72 characters with no more than 72 UTF-8 bytes." });
        if (userType is not ("creator" or "runner" or "both"))
            return BadRequest(new AuthResponse { Success = false, Message = "Choose a valid account type." });
        if (!request.TermsAccepted || !request.PrivacyAccepted ||
            !string.Equals(request.TermsVersion, CurrentTermsVersion, StringComparison.Ordinal) ||
            !string.Equals(request.PrivacyVersion, CurrentPrivacyVersion, StringComparison.Ordinal))
            return BadRequest(new AuthResponse { Success = false, Message = "Review and accept the current terms and privacy notice to create an account." });
        if (!_userPolicyService.IsValidPhone(request.PhoneNumber))
            return BadRequest(new AuthResponse { Success = false, Message = "Enter a valid South African phone number." });
        if (!_userPolicyService.TryGetAdultDateOfBirthFromSouthAfricanId(idNumber, out var derivedDateOfBirth))
            return BadRequest(new AuthResponse { Success = false, Message = "Enter a valid South African ID number for an adult aged 18 or older." });
        if (string.IsNullOrWhiteSpace(address) || address.Length < 2 || address.Length > 200 ||
            _userPolicyService.IsPlaceholderAddress(address))
            return BadRequest(new AuthResponse { Success = false, Message = "Enter a valid area or address." });
        if (await _context.Users.AnyAsync(u => u.Email.ToLower() == email))
            return Conflict(new AuthResponse { Success = false, Message = "We couldn't create an account with these details. Try signing in or resetting your password." });
        if (await _context.Users.AnyAsync(u => u.IdNumber == idNumber))
            return Conflict(new AuthResponse { Success = false, Message = "We couldn't create an account with these details. Contact support if you believe this is an error." });

        var normalizedPhone = NormalizeSouthAfricanPhone(request.PhoneNumber);
        if (normalizedPhone == null)
            return BadRequest(new AuthResponse { Success = false, Message = "Enter a valid South African mobile number." });

        var verificationToken = CreateSecureToken();
        var user = new User
        {
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            PhoneNumber = normalizedPhone,
            UserType = userType,
            IdNumber = idNumber,
            Address = address,
            // Ignore client DOB: the ID prefix is parsed and age checked on the server.
            DateOfBirth = derivedDateOfBirth,
            Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsVerified = false,
            ProfileCompleted = false,
            EmailVerified = false,
            PhoneVerified = false,
            TermsAcceptedAt = now,
            TermsVersion = request.TermsVersion.Trim(),
            PrivacyAcceptedAt = now,
            PrivacyVersion = request.PrivacyVersion.Trim(),
            MarketingConsent = request.MarketingConsent,
            MarketingConsentUpdatedAt = request.MarketingConsent ? now : null,
            EmailVerificationToken = HashToken(verificationToken),
            EmailVerificationExpiry = now.AddHours(24),
            SecurityStamp = CreateSecureToken(),
            Roles = "User",
            CreatedAt = now
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        try
        {
            await SendVerificationEmailAsync(user.Email, verificationToken, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Registration verification email could not be sent for user {UserId}", user.Id);
        }

        // Do not fire-and-forget work that uses the request-scoped DbContext.
        // Await the admin notification while the request scope is alive, but do
        // not fail registration if the notification subsystem is unavailable.
        try
        {
            await _notificationService.NotifyAdminsAsync(
                "new_user",
                "New User Registered",
                $"{user.FirstName} {user.LastName} ({user.Email}) joined as {user.UserType ?? "user"}"
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify admins about newly registered user {UserId}", user.Id);
        }

        // Do not issue a session during registration. Email/phone verification is
        // still pending and this API must not imply the new account is verified.
        return Ok(new AuthResponse
        {
            Success = true,
            Token = null,
            User = new UserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                ProfileCompleted = _userPolicyService.IsProfileComplete(user),
                ProfileCompletion = _userPolicyService.GetProfileCompletion(user),
                MissingProfileFields = _userPolicyService.GetMissingProfileFields(user).ToArray(),
                CanCreateTasks = _userPolicyService.CanCreateTasks(user),
                CanAcceptTasks = _userPolicyService.CanAcceptTasks(user),
                Rating = user.Rating,
                CompletedTasks = user.CompletedTasks,
                Roles = user.Roles,
                IsAdmin = user.Roles?.Contains("Admin") == true,
                UserType = user.UserType,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt,
                IsVerified = user.IsVerified
            },
            Message = "Registration successful. Check your email for a verification link before signing in."
        });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("verify-email")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> VerifyEmail([FromBody] VerifyEmailRequest request)
    {
        var tokenHash = HashToken(request.Token);
        var user = await _context.Users.FirstOrDefaultAsync(u =>
            u.EmailVerificationToken == tokenHash &&
            u.EmailVerificationExpiry != null &&
            u.EmailVerificationExpiry > DateTime.UtcNow &&
            !u.IsArchived);
        if (user == null)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "This verification link is invalid or has expired. Request a new one." });

        user.EmailVerified = true;
        user.EmailVerificationToken = null;
        user.EmailVerificationExpiry = null;
        user.SecurityStamp = CreateSecureToken();
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Email verified. You can now sign in." });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("resend-verification")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> ResendVerification([FromBody] ResendVerificationRequest request)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail && !u.IsArchived);
        if (user != null && !user.EmailVerified)
        {
            var token = CreateSecureToken();
            user.EmailVerificationToken = HashToken(token);
            user.EmailVerificationExpiry = DateTime.UtcNow.AddHours(24);
            await _context.SaveChangesAsync();
            try { await SendVerificationEmailAsync(user.Email, token, HttpContext.RequestAborted); }
            catch (Exception ex) { _logger.LogWarning(ex, "Verification resend failed for user {UserId}", user.Id); }
        }

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "If the account exists and needs verification, an email will be sent shortly." });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail && !u.IsArchived);
        if (user != null)
        {
            var token = CreateSecureToken();
            user.PasswordResetTokenHash = HashToken(token);
            user.PasswordResetExpiry = DateTime.UtcNow.AddMinutes(30);
            await _context.SaveChangesAsync();
            try { await SendPasswordResetEmailAsync(user.Email, token, HttpContext.RequestAborted); }
            catch (Exception ex) { _logger.LogWarning(ex, "Password reset email could not be sent for user {UserId}", user.Id); }
        }

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "If an account matches that email, password reset instructions will be sent shortly." });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        if (request.NewPassword.Length < 12 || request.NewPassword.Length > 72 ||
            Encoding.UTF8.GetByteCount(request.NewPassword) > 72)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Use a password between 12 and 72 characters and no more than 72 UTF-8 bytes." });

        var tokenHash = HashToken(request.Token);
        var user = await _context.Users.FirstOrDefaultAsync(u =>
            u.PasswordResetTokenHash == tokenHash &&
            u.PasswordResetExpiry != null &&
            u.PasswordResetExpiry > DateTime.UtcNow &&
            !u.IsArchived);
        if (user == null)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "This password reset link is invalid or has expired. Request a new one." });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.EmailVerified = true;
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiry = null;
        user.EmailVerificationToken = null;
        user.EmailVerificationExpiry = null;
        user.SecurityStamp = CreateSecureToken();
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Password reset successfully. Please sign in with your new password." });
    }

    [HttpPost("refresh")]
    [Authorize]
    public async Task<ActionResult<AuthResponse>> Refresh()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(userIdClaim, out var userId)) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        return Ok(new AuthResponse
        {
            Success = true,
            Token = GenerateJwtToken(user),
            User = new UserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                ProfileCompleted = user.ProfileCompleted,
                ProfileCompletion = _userPolicyService.GetProfileCompletion(user),
                MissingProfileFields = _userPolicyService.GetMissingProfileFields(user).ToArray(),
                CanCreateTasks = _userPolicyService.CanCreateTasks(user),
                CanAcceptTasks = _userPolicyService.CanAcceptTasks(user),
                Rating = user.Rating,
                CompletedTasks = user.CompletedTasks,
                Roles = user.Roles,
                IsAdmin = user.Roles?.Contains("Admin") == true,
                UserType = user.UserType,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt,
                IsVerified = user.IsVerified
            },
            Message = "Session refreshed"
        });
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<bool>>> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(userIdClaim, out var userId)) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        if (request.NewPassword.Length < 12 || request.NewPassword.Length > 72 ||
            System.Text.Encoding.UTF8.GetByteCount(request.NewPassword) > 72)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Use a password between 12 and 72 characters and no more than 72 UTF-8 bytes." });

        if (string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
            return Ok(new ApiResponse<bool> { Success = false, Message = "New password must be different from the current password" });

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return Ok(new ApiResponse<bool> { Success = false, Message = "Current password is incorrect" });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.SecurityStamp = CreateSecureToken();
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Password changed successfully" });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request)
    {
        if (request == null)
            return BadRequest("Invalid request");
        
        // Normalize the email identifier without accepting URI prefixes.
        var cleanEmail = request.Email?.Trim().ToLowerInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == cleanEmail);
        // Perform a hash verification for unknown accounts to reduce timing-based enumeration.
        var hashToVerify = user?.PasswordHash ?? DummyPasswordHash;
        var passwordValid = BCrypt.Net.BCrypt.Verify(request.Password ?? string.Empty, hashToVerify);

        if (user == null || user.IsArchived || !passwordValid)
        {
            return Unauthorized(new AuthResponse
            {
                Success = false,
                Message = "Invalid email or password"
            });
        }

        var isAdminAccount = user.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(role => string.Equals(role.Trim(), "Admin", StringComparison.OrdinalIgnoreCase)) == true;
        if (!isAdminAccount && !user.EmailVerified)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new AuthResponse
            {
                Success = false,
                Message = "Please verify your email before signing in. You can request a new verification email."
            });
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var token = GenerateJwtToken(user);

        _logger.LogInformation("Successful login for user id {UserId}", user.Id);
        return Ok(new AuthResponse
        {
            Success = true,
            Token = token,
            User = new UserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                ProfileCompleted = user.ProfileCompleted,
                ProfileCompletion = _userPolicyService.GetProfileCompletion(user),
                MissingProfileFields = _userPolicyService.GetMissingProfileFields(user).ToArray(),
                CanCreateTasks = _userPolicyService.CanCreateTasks(user),
                CanAcceptTasks = _userPolicyService.CanAcceptTasks(user),
                Rating = user.Rating,
                CompletedTasks = user.CompletedTasks,
                Roles = user.Roles,
                IsAdmin = user.Roles?.Contains("Admin") == true,
                UserType = user.UserType,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt,
                IsVerified = user.IsVerified
            },
            Message = "Login successful"
        });
    }

    private static string CreateSecureToken() =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private string GetPublicFrontendUrl()
    {
        var value = Environment.GetEnvironmentVariable("DFY_PUBLIC_URL")
            ?? _configuration["Frontend:BaseUrl"];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("DFY_PUBLIC_URL or Frontend:BaseUrl must be configured for account emails.");
        return value.TrimEnd('/');
    }

    private Task SendVerificationEmailAsync(string email, string token, CancellationToken cancellationToken)
    {
        var link = $"{GetPublicFrontendUrl()}/verify-email?token={Uri.EscapeDataString(token)}";
        var body = $"Welcome to DoForYou.\n\nVerify your email address using this link (valid for 24 hours):\n{link}\n\nIf you did not create this account, you can ignore this email.";
        return _emailSender.SendAsync(email, "Verify your DoForYou email", body, cancellationToken);
    }

    private Task SendPasswordResetEmailAsync(string email, string token, CancellationToken cancellationToken)
    {
        var link = $"{GetPublicFrontendUrl()}/reset-password?token={Uri.EscapeDataString(token)}";
        var body = $"A password reset was requested for your DoForYou account.\n\nUse this link within 30 minutes to choose a new password:\n{link}\n\nIf you did not request this, you can ignore this email. Your password will not change.";
        return _emailSender.SendAsync(email, "Reset your DoForYou password", body, cancellationToken);
    }

    private static readonly string DummyPasswordHash = BCrypt.Net.BCrypt.HashPassword(
        "not-a-real-user-password-never-use-this-to-sign-in", workFactor: 11);

    private static string? NormalizeSouthAfricanPhone(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("27", StringComparison.Ordinal)) digits = digits[2..];
        else if (digits.StartsWith('0')) digits = digits[1..];
        return digits.Length == 9 && digits[0] is >= '6' and <= '8' ? "0" + digits : null;
    }

    private string GenerateJwtToken(User user)
    {
        var configuredKey = _configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_KEY");
        if (string.IsNullOrWhiteSpace(configuredKey))
            throw new InvalidOperationException("JWT signing key is not configured.");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuredKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim("security_stamp", user.SecurityStamp ?? string.Empty)
        };

        // Add each role as a separate claim
        if (!string.IsNullOrEmpty(user.Roles))
        {
            var roles = user.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role.Trim()));
            }
        }

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"] ?? "DoForYou",
            audience: _configuration["Jwt:Audience"] ?? "DoForYou",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static DateTime? ConvertToUtc(DateTime? dateTime)
    {
        if (!dateTime.HasValue) return null;
        
        return dateTime.Value.Kind switch
        {
            DateTimeKind.Utc => dateTime.Value,
            DateTimeKind.Local => dateTime.Value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dateTime.Value, DateTimeKind.Utc),
            _ => dateTime.Value
        };
    }
}