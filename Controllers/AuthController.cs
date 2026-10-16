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
using Google.Apis.Auth;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly INotificationService _notificationService;
    private readonly IUserPolicyService _userPolicyService;
    private readonly IEmailSender _emailSender;
    private readonly IAuthenticatorService _authenticatorService;
    private const string CurrentTermsVersion = "terms-v1";
    private const string CurrentPrivacyVersion = "privacy-v1";

    public AuthController(AppDbContext context, IConfiguration configuration, ILogger<AuthController> logger, INotificationService notificationService, IUserPolicyService userPolicyService, IEmailSender emailSender, IAuthenticatorService authenticatorService)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
        _notificationService = notificationService;
        _userPolicyService = userPolicyService;
        _emailSender = emailSender;
        _authenticatorService = authenticatorService;
    }

    [EnableRateLimiting("auth")]
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var firstName = request.FirstName.Trim();
        var lastName = request.LastName.Trim();
        var userType = request.UserType.Trim().ToLowerInvariant();
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
        if (await _context.Users.AnyAsync(u => (u.NormalizedEmail == email || (u.NormalizedEmail == null && u.Email.ToLower() == email))))
            return Conflict(new AuthResponse { Success = false, Message = "We couldn't create an account with these details. Try signing in or resetting your password." });
        var normalizedPhone = NormalizeSouthAfricanPhone(request.PhoneNumber);
        if (normalizedPhone == null)
            return BadRequest(new AuthResponse { Success = false, Message = "Enter a valid South African mobile number." });

        var verificationToken = CreateSecureToken();
        var user = new User
        {
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            NormalizedEmail = email,
            PhoneNumber = normalizedPhone,
            UserType = userType,
            // Identity and address are collected later through the appropriate onboarding flow.
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
            EmailVerificationSentAt = now,
            EmailVerificationSendWindowStartAt = now,
            EmailVerificationSendCount = 1,
            SecurityStamp = CreateSecureToken(),
            Roles = "User",
            CreatedAt = now
        };

        _context.Users.Add(user);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // The unique normalized-email index is authoritative under concurrent sign-ups.
            return Conflict(new AuthResponse { Success = false, Message = "We couldn't create an account with these details. Try signing in or resetting your password." });
        }

        try
        {
            await SendVerificationEmailAsync(user.Email, verificationToken, HttpContext.RequestAborted);
        }
        catch (Exception)
        {
            _logger.LogWarning("Registration verification email could not be sent for user {UserId}", user.Id);
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

        // Do not issue a session during registration. Email verification is pending;
        // a contact phone number is not treated as a verification factor.
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
                IsVerified = user.IsVerified,
                EmailVerified = user.EmailVerified,
                PhoneVerified = user.PhoneVerified,
                IdentityVerificationStatus = user.IdentityVerificationStatus,
                AuthenticatorTwoFactorEnabled = user.AuthenticatorTwoFactorEnabled
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

        var now = DateTime.UtcNow;
        var newSecurityStamp = CreateSecureToken();
        if (_context.Database.IsRelational())
        {
            var affected = await _context.Users
                .Where(u => u.Id == user.Id &&
                            u.EmailVerificationToken == tokenHash &&
                            u.EmailVerificationExpiry != null &&
                            u.EmailVerificationExpiry > now &&
                            !u.IsArchived)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.EmailVerified, true)
                    .SetProperty(u => u.EmailVerificationToken, (string?)null)
                    .SetProperty(u => u.EmailVerificationExpiry, (DateTime?)null)
                    .SetProperty(u => u.EmailVerificationSentAt, (DateTime?)null)
                    .SetProperty(u => u.SecurityStamp, newSecurityStamp)
                    .SetProperty(u => u.FailedLoginAttempts, 0)
                    .SetProperty(u => u.FailedLoginWindowStartAt, (DateTime?)null)
                    .SetProperty(u => u.LoginLockoutUntil, (DateTime?)null),
                    HttpContext.RequestAborted);
            if (affected == 0)
                return BadRequest(new ApiResponse<bool> { Success = false, Message = "This verification link is invalid or has expired. Request a new one." });
        }
        else
        {
            user.EmailVerified = true;
            user.EmailVerificationToken = null;
            user.EmailVerificationExpiry = null;
            user.EmailVerificationSentAt = null;
            user.SecurityStamp = newSecurityStamp;
            user.FailedLoginAttempts = 0;
            user.FailedLoginWindowStartAt = null;
            user.LoginLockoutUntil = null;
            await _context.SaveChangesAsync();
        }

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Email verified. You can now sign in." });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("resend-verification")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> ResendVerification([FromBody] ResendVerificationRequest request)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u => (u.NormalizedEmail == normalizedEmail || (u.NormalizedEmail == null && u.Email.ToLower() == normalizedEmail)) && !u.IsArchived);
        var now = DateTime.UtcNow;

        if (user != null && !user.EmailVerified)
        {
            // Reset the rolling 24-hour budget only when its persisted window has expired.
            if (!user.EmailVerificationSendWindowStartAt.HasValue ||
                user.EmailVerificationSendWindowStartAt.Value <= now.AddHours(-24))
            {
                if (_context.Database.IsRelational())
                {
                    await _context.Users
                        .Where(u => u.Id == user.Id &&
                            (!u.EmailVerificationSendWindowStartAt.HasValue ||
                             u.EmailVerificationSendWindowStartAt <= now.AddHours(-24)))
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(u => u.EmailVerificationSendWindowStartAt, now)
                            .SetProperty(u => u.EmailVerificationSendCount, 0),
                            HttpContext.RequestAborted);
                }
                else
                {
                    user.EmailVerificationSendWindowStartAt = now;
                    user.EmailVerificationSendCount = 0;
                    await _context.SaveChangesAsync();
                }
            }

            var token = CreateSecureToken();
            var tokenHash = HashToken(token);
            var canSend = _context.Users.Where(u =>
                u.Id == user.Id &&
                !u.IsArchived &&
                !u.EmailVerified &&
                u.EmailVerificationSendWindowStartAt.HasValue &&
                u.EmailVerificationSendWindowStartAt > now.AddHours(-24) &&
                u.EmailVerificationSendCount < 5 &&
                (!u.EmailVerificationSentAt.HasValue ||
                 u.EmailVerificationSentAt <= now.AddSeconds(-60)));

            var sent = false;
            if (_context.Database.IsRelational())
            {
                var affected = await canSend.ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.EmailVerificationToken, tokenHash)
                    .SetProperty(u => u.EmailVerificationExpiry, now.AddHours(24))
                    .SetProperty(u => u.EmailVerificationSentAt, now)
                    .SetProperty(u => u.EmailVerificationSendCount, u => u.EmailVerificationSendCount + 1),
                    HttpContext.RequestAborted);
                sent = affected == 1;
            }
            else
            {
                if (!user.EmailVerified &&
                    user.EmailVerificationSendWindowStartAt.HasValue &&
                    user.EmailVerificationSendWindowStartAt.Value > now.AddHours(-24) &&
                    user.EmailVerificationSendCount < 5 &&
                    (!user.EmailVerificationSentAt.HasValue ||
                     user.EmailVerificationSentAt.Value <= now.AddSeconds(-60)))
                {
                    user.EmailVerificationToken = tokenHash;
                    user.EmailVerificationExpiry = now.AddHours(24);
                    user.EmailVerificationSentAt = now;
                    user.EmailVerificationSendCount++;
                    await _context.SaveChangesAsync();
                    sent = true;
                }
            }

            if (sent)
            {
                try { await SendVerificationEmailAsync(user.Email, token, HttpContext.RequestAborted); }
                catch (Exception) { _logger.LogWarning("Verification resend failed for user {UserId}", user.Id); }
            }
        }

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "If the account exists and needs verification, an email will be sent shortly." });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u =>
            (u.NormalizedEmail == normalizedEmail ||
             (u.NormalizedEmail == null && u.Email.ToLower() == normalizedEmail)) &&
            !u.IsArchived);
        var now = DateTime.UtcNow;

        if (user != null)
        {
            // Keep a persisted per-account rolling 24-hour budget. The public response
            // remains identical for unknown accounts, cooldowns, and exhausted quotas.
            var resetWindow = !user.PasswordResetSendWindowStartAt.HasValue ||
                              user.PasswordResetSendWindowStartAt.Value <= now.AddHours(-24);
            if (resetWindow)
            {
                if (_context.Database.IsRelational())
                {
                    await _context.Users
                        .Where(u => u.Id == user.Id &&
                            (!u.PasswordResetSendWindowStartAt.HasValue ||
                             u.PasswordResetSendWindowStartAt <= now.AddHours(-24)))
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(u => u.PasswordResetSendWindowStartAt, now)
                            .SetProperty(u => u.PasswordResetSendCount, 0),
                            HttpContext.RequestAborted);
                }
                else
                {
                    user.PasswordResetSendWindowStartAt = now;
                    user.PasswordResetSendCount = 0;
                }
            }

            var token = CreateSecureToken();
            var tokenHash = HashToken(token);
            var canSend = _context.Users.Where(u =>
                u.Id == user.Id &&
                !u.IsArchived &&
                u.PasswordResetSendWindowStartAt.HasValue &&
                u.PasswordResetSendWindowStartAt > now.AddHours(-24) &&
                u.PasswordResetSendCount < 5 &&
                (!u.PasswordResetSentAt.HasValue ||
                 u.PasswordResetSentAt <= now.AddSeconds(-60)));

            var sent = false;
            if (_context.Database.IsRelational())
            {
                var affected = await canSend.ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.PasswordResetTokenHash, tokenHash)
                    .SetProperty(u => u.PasswordResetExpiry, now.AddMinutes(30))
                    .SetProperty(u => u.PasswordResetSentAt, now)
                    .SetProperty(u => u.PasswordResetSendCount, u => u.PasswordResetSendCount + 1),
                    HttpContext.RequestAborted);
                sent = affected == 1;
            }
            else if (user.PasswordResetSendWindowStartAt.HasValue &&
                     user.PasswordResetSendWindowStartAt.Value > now.AddHours(-24) &&
                     user.PasswordResetSendCount < 5 &&
                     (!user.PasswordResetSentAt.HasValue ||
                      user.PasswordResetSentAt.Value <= now.AddSeconds(-60)))
            {
                user.PasswordResetTokenHash = tokenHash;
                user.PasswordResetExpiry = now.AddMinutes(30);
                user.PasswordResetSentAt = now;
                user.PasswordResetSendCount++;
                await _context.SaveChangesAsync();
                sent = true;
            }

            if (sent)
            {
                try { await SendPasswordResetEmailAsync(user.Email, token, HttpContext.RequestAborted); }
                catch (Exception) { _logger.LogWarning("Password reset email could not be sent for user {UserId}", user.Id); }
            }
        }

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "If an account matches that email, password reset instructions will be sent shortly."
        });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> ResetPassword([FromBody] AccountResetPasswordRequest request)
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

        var now = DateTime.UtcNow;
        var newPasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        var newSecurityStamp = CreateSecureToken();
        if (_context.Database.IsRelational())
        {
            var affected = await _context.Users
                .Where(u => u.Id == user.Id &&
                            u.PasswordResetTokenHash == tokenHash &&
                            u.PasswordResetExpiry != null &&
                            u.PasswordResetExpiry > now &&
                            !u.IsArchived)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.PasswordHash, newPasswordHash)
                    .SetProperty(u => u.EmailVerified, true)
                    .SetProperty(u => u.PasswordResetTokenHash, (string?)null)
                    .SetProperty(u => u.PasswordResetExpiry, (DateTime?)null)
                    .SetProperty(u => u.PasswordResetSentAt, (DateTime?)null)
                    .SetProperty(u => u.EmailVerificationToken, (string?)null)
                    .SetProperty(u => u.EmailVerificationExpiry, (DateTime?)null)
                    .SetProperty(u => u.EmailVerificationSentAt, (DateTime?)null)
                    .SetProperty(u => u.SecurityStamp, newSecurityStamp)
                    .SetProperty(u => u.FailedLoginAttempts, 0)
                    .SetProperty(u => u.FailedLoginWindowStartAt, (DateTime?)null)
                    .SetProperty(u => u.LoginLockoutUntil, (DateTime?)null),
                    HttpContext.RequestAborted);
            if (affected == 0)
                return BadRequest(new ApiResponse<bool> { Success = false, Message = "This password reset link is invalid or has expired. Request a new one." });
        }
        else
        {
            user.PasswordHash = newPasswordHash;
            user.EmailVerified = true;
            user.PasswordResetTokenHash = null;
            user.PasswordResetExpiry = null;
            user.PasswordResetSentAt = null;
            user.EmailVerificationToken = null;
            user.EmailVerificationExpiry = null;
            user.EmailVerificationSentAt = null;
            user.SecurityStamp = newSecurityStamp;
            user.FailedLoginAttempts = 0;
            user.FailedLoginWindowStartAt = null;
            user.LoginLockoutUntil = null;
            await _context.SaveChangesAsync();
        }

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Password reset successfully. Please sign in with your new password." });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("google")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> GoogleLogin([FromBody] GoogleAuthRequest request)
    {
        var googlePayload = await ValidateGoogleIdTokenAsync(request.IdToken);
        if (googlePayload == null || googlePayload.EmailVerified != true ||
            string.IsNullOrWhiteSpace(googlePayload.Subject) ||
            string.IsNullOrWhiteSpace(googlePayload.Email))
            return Unauthorized(new AuthResponse { Success = false, Message = "Google sign-in could not be verified. Please try again." });

        var normalizedEmail = googlePayload.Email.Trim().ToLowerInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u =>
            u.GoogleSubject == googlePayload.Subject ||
            u.NormalizedEmail == normalizedEmail ||
            (u.NormalizedEmail == null && u.Email.ToLower() == normalizedEmail));
        if (user == null)
            return Conflict(new AuthResponse
            {
                Success = false,
                RequiresAccountSetup = true,
                Message = "Choose an account type and complete the signup form to create your DoForYou account."
            });
        if (user.IsArchived)
            return Unauthorized(new AuthResponse { Success = false, Message = "Google sign-in could not be completed for this account." });
        if (user.LoginLockoutUntil.HasValue && user.LoginLockoutUntil.Value > DateTime.UtcNow)
            return Unauthorized(new AuthResponse { Success = false, Message = "Google sign-in could not be completed for this account." });
        if (!string.IsNullOrWhiteSpace(user.GoogleSubject) &&
            !string.Equals(user.GoogleSubject, googlePayload.Subject, StringComparison.Ordinal))
            return Conflict(new AuthResponse { Success = false, Message = "This Google account cannot be linked to the selected DoForYou account." });

        // Google has independently asserted control of this verified email address.
        // Link by stable subject only after validating token signature, issuer, expiry and audience.
        user.GoogleSubject = googlePayload.Subject;
        if (!user.EmailVerified)
        {
            user.EmailVerified = true;
            user.SecurityStamp = CreateSecureToken();
        }
        if (string.IsNullOrWhiteSpace(user.NormalizedEmail))
            user.NormalizedEmail = user.Email.Trim().ToLowerInvariant();

        if (user.AuthenticatorTwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.SecondFactorCode))
                return Unauthorized(new AuthResponse { Success = false, RequiresTwoFactor = true, Message = "Enter the code from your authenticator app or a recovery code." });
            if (!await ValidateSecondFactorAsync(user, request.SecondFactorCode))
            {
                await RecordFailedLoginAsync(user.Id);
                return Unauthorized(new AuthResponse { Success = false, RequiresTwoFactor = true, Message = "The authenticator or recovery code is invalid." });
            }
            user.LastTwoFactorVerifiedAt = DateTime.UtcNow;
        }

        user.FailedLoginAttempts = 0;
        user.FailedLoginWindowStartAt = null;
        user.LoginLockoutUntil = null;
        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        _logger.LogInformation("Successful Google login for user id {UserId}", user.Id);
        return Ok(CreateSessionResponse(user, "Google sign-in successful."));
    }

    [EnableRateLimiting("auth")]
    [HttpPost("google/register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> RegisterWithGoogle([FromBody] GoogleRegisterRequest request)
    {
        var googlePayload = await ValidateGoogleIdTokenAsync(request.IdToken);
        if (googlePayload == null || googlePayload.EmailVerified != true ||
            string.IsNullOrWhiteSpace(googlePayload.Subject) ||
            string.IsNullOrWhiteSpace(googlePayload.Email))
            return Unauthorized(new AuthResponse { Success = false, Message = "Google sign-up could not be verified. Please try again." });

        var firstName = string.IsNullOrWhiteSpace(request.FirstName) ? googlePayload.GivenName?.Trim() : request.FirstName.Trim();
        var lastName = string.IsNullOrWhiteSpace(request.LastName) ? googlePayload.FamilyName?.Trim() : request.LastName.Trim();
        var normalizedEmail = googlePayload.Email.Trim().ToLowerInvariant();
        var userType = request.UserType.Trim().ToLowerInvariant();
        var normalizedPhone = NormalizeSouthAfricanPhone(request.PhoneNumber);
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Length > 50 ||
            string.IsNullOrWhiteSpace(lastName) || lastName.Length > 50)
            return BadRequest(new AuthResponse { Success = false, Message = "Enter your first and last name to continue." });
        if (userType is not ("creator" or "runner" or "both"))
            return BadRequest(new AuthResponse { Success = false, Message = "Choose Creator, Runner, or Both." });
        if (normalizedPhone == null)
            return BadRequest(new AuthResponse { Success = false, Message = "Enter a valid South African mobile number." });
        if (!request.TermsAccepted || !request.PrivacyAccepted ||
            !string.Equals(request.TermsVersion, CurrentTermsVersion, StringComparison.Ordinal) ||
            !string.Equals(request.PrivacyVersion, CurrentPrivacyVersion, StringComparison.Ordinal))
            return BadRequest(new AuthResponse { Success = false, Message = "Review and accept the current terms and privacy notice to create an account." });

        if (await _context.Users.AnyAsync(u =>
            u.NormalizedEmail == normalizedEmail ||
            u.GoogleSubject == googlePayload.Subject ||
            (u.NormalizedEmail == null && u.Email.ToLower() == normalizedEmail)))
            return Conflict(new AuthResponse { Success = false, Message = "We couldn't create an account with these details. Try signing in instead." });

        var now = DateTime.UtcNow;
        var user = new User
        {
            FirstName = firstName,
            LastName = lastName,
            Email = normalizedEmail,
            NormalizedEmail = normalizedEmail,
            PhoneNumber = normalizedPhone,
            UserType = userType,
            GoogleSubject = googlePayload.Subject,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            IsVerified = false,
            ProfileCompleted = false,
            EmailVerified = true,
            PhoneVerified = false,
            IdentityVerificationStatus = "not_started",
            AuthenticatorTwoFactorEnabled = false,
            TermsAcceptedAt = now,
            TermsVersion = request.TermsVersion.Trim(),
            PrivacyAcceptedAt = now,
            PrivacyVersion = request.PrivacyVersion.Trim(),
            MarketingConsent = request.MarketingConsent,
            MarketingConsentUpdatedAt = request.MarketingConsent ? now : null,
            SecurityStamp = CreateSecureToken(),
            Roles = "User",
            CreatedAt = now
        };
        _context.Users.Add(user);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            return Conflict(new AuthResponse { Success = false, Message = "We couldn't create an account with these details. Try signing in instead." });
        }

        try
        {
            await _notificationService.NotifyAdminsAsync(
                "new_user",
                "New User Registered",
                $"{user.FirstName} {user.LastName} ({user.Email}) joined as {user.UserType} using Google");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify admins about Google registration for user {UserId}", user.Id);
        }

        _logger.LogInformation("Successful Google registration for user id {UserId}", user.Id);
        return Ok(CreateSessionResponse(user, "Account created with Google. Your email is verified."));
    }

    [HttpGet("two-factor/status")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> GetTwoFactorStatus()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null || user.IsArchived) return Unauthorized();
        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                enabled = user.AuthenticatorTwoFactorEnabled,
                enrolledAt = user.AuthenticatorEnrolledAt,
                remainingRecoveryCodes = ReadRecoveryCodeCount(user.AuthenticatorRecoveryCodeHashes),
                encryptionConfigured = _authenticatorService.IsConfigured
            }
        });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("two-factor/setup")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> SetupTwoFactor([FromBody] TwoFactorSetupRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null || user.IsArchived) return Unauthorized();
        if (user.AuthenticatorTwoFactorEnabled)
            return Conflict(new ApiResponse<object> { Success = false, Message = "Authenticator two-factor authentication is already enabled." });
        if (!_authenticatorService.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ApiResponse<object> { Success = false, Message = "Authenticator setup is not configured on this staging API yet." });

        if (user.GoogleSubject == null)
        {
            if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
                !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
                return Unauthorized(new ApiResponse<object> { Success = false, Message = "Confirm your current password to set up an authenticator." });
        }
        else if (!user.LastLoginAt.HasValue || user.LastLoginAt.Value < DateTime.UtcNow.AddMinutes(-10))
        {
            return Unauthorized(new ApiResponse<object> { Success = false, Message = "Sign in again before setting up an authenticator." });
        }

        var secret = _authenticatorService.CreateSecret();
        user.PendingAuthenticatorSecretEncrypted = _authenticatorService.EncryptSecret(secret);
        user.PendingAuthenticatorSecretExpiry = DateTime.UtcNow.AddMinutes(10);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                secret,
                provisioningUri = _authenticatorService.CreateProvisioningUri(user.Email, secret),
                issuer = "DoForYou",
                stepSeconds = 30,
                digits = 6
            },
            Message = "Add this account to your authenticator app, then enter the current six-digit code to confirm setup. The secret is shown only during setup."
        });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("two-factor/confirm")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> ConfirmTwoFactor([FromBody] TwoFactorCodeRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null || user.IsArchived) return Unauthorized();
        if (!_authenticatorService.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ApiResponse<object> { Success = false, Message = "Authenticator setup is not configured on this staging API yet." });
        if (user.AuthenticatorTwoFactorEnabled)
            return Conflict(new ApiResponse<object> { Success = false, Message = "Authenticator two-factor authentication is already enabled." });

        var pendingEncrypted = user.PendingAuthenticatorSecretEncrypted;
        var now = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(pendingEncrypted) ||
            !user.PendingAuthenticatorSecretExpiry.HasValue ||
            user.PendingAuthenticatorSecretExpiry.Value <= now)
        {
            user.PendingAuthenticatorSecretEncrypted = null;
            user.PendingAuthenticatorSecretExpiry = null;
            await _context.SaveChangesAsync();
            return Conflict(new ApiResponse<object> { Success = false, Message = "Authenticator setup has expired. Start setup again." });
        }

        string secret;
        try { secret = _authenticatorService.DecryptSecret(pendingEncrypted); }
        catch (CryptographicException) { return StatusCode(503, new ApiResponse<object> { Success = false, Message = "Authenticator setup cannot be decrypted. Contact support." }); }
        catch (FormatException) { return StatusCode(503, new ApiResponse<object> { Success = false, Message = "Authenticator setup cannot be decrypted. Contact support." }); }
        if (!_authenticatorService.VerifyCode(secret, request.Code, new DateTimeOffset(now)))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "The authenticator code is invalid or expired. Check your device time and try again." });

        var recoveryCodes = Enumerable.Range(0, 10).Select(_ => _authenticatorService.GenerateRecoveryCode()).ToArray();
        var serializedRecoveryHashes = System.Text.Json.JsonSerializer.Serialize(
            recoveryCodes.Select(_authenticatorService.HashRecoveryCode).ToArray());
        var newSecurityStamp = CreateSecureToken();

        if (_context.Database.IsRelational())
        {
            var affected = await _context.Users
                .Where(u => u.Id == user.Id &&
                            !u.IsArchived &&
                            !u.AuthenticatorTwoFactorEnabled &&
                            u.PendingAuthenticatorSecretEncrypted == pendingEncrypted &&
                            u.PendingAuthenticatorSecretExpiry != null &&
                            u.PendingAuthenticatorSecretExpiry > now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.AuthenticatorSecretEncrypted, pendingEncrypted)
                    .SetProperty(u => u.PendingAuthenticatorSecretEncrypted, (string?)null)
                    .SetProperty(u => u.PendingAuthenticatorSecretExpiry, (DateTime?)null)
                    .SetProperty(u => u.AuthenticatorRecoveryCodeHashes, serializedRecoveryHashes)
                    .SetProperty(u => u.AuthenticatorEnrolledAt, (DateTime?)now)
                    .SetProperty(u => u.AuthenticatorTwoFactorEnabled, true)
                    .SetProperty(u => u.SecurityStamp, newSecurityStamp),
                    HttpContext.RequestAborted);
            if (affected != 1)
                return Conflict(new ApiResponse<object> { Success = false, Message = "Authenticator setup has expired or was already confirmed. Start setup again." });
        }
        else
        {
            user.AuthenticatorSecretEncrypted = pendingEncrypted;
            user.PendingAuthenticatorSecretEncrypted = null;
            user.PendingAuthenticatorSecretExpiry = null;
            user.AuthenticatorRecoveryCodeHashes = serializedRecoveryHashes;
            user.AuthenticatorEnrolledAt = now;
            user.AuthenticatorTwoFactorEnabled = true;
            user.SecurityStamp = newSecurityStamp;
            await _context.SaveChangesAsync();
        }

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { recoveryCodes, requiresSignInAgain = true },
            Message = "Authenticator enabled. Save these one-time recovery codes now. They will not be shown again; sign in again to refresh your session."
        });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("two-factor/cancel-setup")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<bool>>> CancelTwoFactorSetup()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null || user.IsArchived) return Unauthorized();
        if (user.AuthenticatorTwoFactorEnabled)
            return Conflict(new ApiResponse<bool> { Success = false, Message = "Authenticator is already enabled; use the disable flow instead." });

        user.PendingAuthenticatorSecretEncrypted = null;
        user.PendingAuthenticatorSecretExpiry = null;
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Pending authenticator setup cancelled." });
    }

    [EnableRateLimiting("auth")]
    [HttpPost("two-factor/disable")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<bool>>> DisableTwoFactor([FromBody] DisableTwoFactorRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null || user.IsArchived) return Unauthorized();
        if (!user.AuthenticatorTwoFactorEnabled)
            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Authenticator two-factor authentication is already disabled." });
        if (user.GoogleSubject == null &&
            (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
             !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash)))
            return Unauthorized(new ApiResponse<bool> { Success = false, Message = "Confirm your current password before disabling authenticator security." });

        if (!await ValidateSecondFactorAsync(user, request.Code))
            return Unauthorized(new ApiResponse<bool> { Success = false, Message = "The authenticator or recovery code is invalid." });

        user.AuthenticatorTwoFactorEnabled = false;
        user.AuthenticatorSecretEncrypted = null;
        user.PendingAuthenticatorSecretEncrypted = null;
        user.AuthenticatorRecoveryCodeHashes = null;
        user.AuthenticatorEnrolledAt = null;
        user.LastTwoFactorVerifiedAt = null;
        user.SecurityStamp = CreateSecureToken();
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Authenticator disabled. Sign in again to refresh your session." });
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<bool>>> Logout()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        // This API uses stateless access tokens rather than per-device refresh-token
        // families, so a logout rotates the shared security stamp and revokes all
        // active sessions for the account. Clients must clear local credentials even
        // if this best-effort request cannot reach the API.
        user.SecurityStamp = CreateSecureToken();
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Signed out. Other active sessions have also been revoked."
        });
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
                IsVerified = user.IsVerified,
                EmailVerified = user.EmailVerified,
                PhoneVerified = user.PhoneVerified,
                IdentityVerificationStatus = user.IdentityVerificationStatus,
                AuthenticatorTwoFactorEnabled = user.AuthenticatorTwoFactorEnabled
            },
            Message = "Session refreshed"
        });
    }

    [EnableRateLimiting("auth")]
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
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "New password must be different from the current password" });

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Current password is incorrect" });

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

        var cleanEmail = request.Email?.Trim().ToLowerInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u =>
            u.NormalizedEmail == cleanEmail ||
            (u.NormalizedEmail == null && u.Email.ToLower() == cleanEmail));

        // Verify against a dummy hash for unknown emails to reduce timing-based enumeration.
        var hashToVerify = user?.PasswordHash ?? DummyPasswordHash;
        var passwordValid = BCrypt.Net.BCrypt.Verify(request.Password ?? string.Empty, hashToVerify);

        if (user == null || user.IsArchived)
        {
            return Unauthorized(new AuthResponse { Success = false, Message = "Invalid email or password" });
        }

        var now = DateTime.UtcNow;
        if (user.LoginLockoutUntil.HasValue && user.LoginLockoutUntil.Value > now)
            return Unauthorized(new AuthResponse { Success = false, Message = "Invalid email or password" });

        if (!passwordValid)
        {
            await RecordFailedLoginAsync(user.Id);
            return Unauthorized(new AuthResponse { Success = false, Message = "Invalid email or password" });
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

        if (user.AuthenticatorTwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.SecondFactorCode))
                return Unauthorized(new AuthResponse { Success = false, RequiresTwoFactor = true, Message = "Enter the code from your authenticator app or a recovery code." });
            if (!await ValidateSecondFactorAsync(user, request.SecondFactorCode))
            {
                await RecordFailedLoginAsync(user.Id);
                return Unauthorized(new AuthResponse { Success = false, RequiresTwoFactor = true, Message = "The authenticator or recovery code is invalid." });
            }
            user.LastTwoFactorVerifiedAt = now;
        }

        user.FailedLoginAttempts = 0;
        user.FailedLoginWindowStartAt = null;
        user.LoginLockoutUntil = null;
        user.LastLoginAt = now;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Successful login for user id {UserId}", user.Id);
        return Ok(CreateSessionResponse(user, "Login successful"));
    }

    private async Task<GoogleJsonWebSignature.Payload?> ValidateGoogleIdTokenAsync(string idToken)
    {
        var configuredIds = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_IDS")
            ?? _configuration["Google:ClientIds"]
            ?? _configuration["Google:ClientId"];
        var audiences = (configuredIds ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (audiences.Length == 0) return null;
        try
        {
            return await GoogleJsonWebSignature.ValidateAsync(
                idToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = audiences });
        }
        catch (InvalidJwtException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async System.Threading.Tasks.Task RecordFailedLoginAsync(int userId)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddMinutes(-15);

        if (_context.Database.IsRelational())
        {
            await _context.Users
                .Where(u => u.Id == userId &&
                            (!u.LoginLockoutUntil.HasValue || u.LoginLockoutUntil <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.FailedLoginWindowStartAt,
                        u => !u.FailedLoginWindowStartAt.HasValue || u.FailedLoginWindowStartAt <= cutoff
                            ? now
                            : u.FailedLoginWindowStartAt)
                    .SetProperty(u => u.FailedLoginAttempts,
                        u => !u.FailedLoginWindowStartAt.HasValue || u.FailedLoginWindowStartAt <= cutoff
                            ? 1
                            : u.FailedLoginAttempts + 1)
                    .SetProperty(u => u.LoginLockoutUntil,
                        u => !u.FailedLoginWindowStartAt.HasValue || u.FailedLoginWindowStartAt <= cutoff
                            ? (DateTime?)null
                            : u.FailedLoginAttempts + 1 >= 10
                                ? now.AddMinutes(15)
                                : u.LoginLockoutUntil),
                    HttpContext.RequestAborted);
            return;
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null || (user.LoginLockoutUntil.HasValue && user.LoginLockoutUntil.Value > now))
            return;

        if (!user.FailedLoginWindowStartAt.HasValue || user.FailedLoginWindowStartAt.Value <= cutoff)
        {
            user.FailedLoginWindowStartAt = now;
            user.FailedLoginAttempts = 1;
            user.LoginLockoutUntil = null;
        }
        else
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= 10)
                user.LoginLockoutUntil = now.AddMinutes(15);
        }

        await _context.SaveChangesAsync();
    }

    private async Task<bool> ValidateSecondFactorAsync(User user, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        // Recovery codes are independently hashed and remain usable when the TOTP
        // encryption key is temporarily unavailable. Consume them with a compare-
        // and-swap update so two concurrent requests cannot spend the same code.
        var previousHashes = user.AuthenticatorRecoveryCodeHashes;
        if (_authenticatorService.TryConsumeRecoveryCode(code, previousHashes, out var remainingHashes))
        {
            if (_context.Database.IsRelational())
            {
                if (string.IsNullOrWhiteSpace(previousHashes)) return false;
                var affected = await _context.Users
                    .Where(u => u.Id == user.Id &&
                                u.AuthenticatorTwoFactorEnabled &&
                                u.AuthenticatorRecoveryCodeHashes == previousHashes)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(u => u.AuthenticatorRecoveryCodeHashes, remainingHashes),
                        HttpContext.RequestAborted);
                if (affected != 1) return false;
            }
            user.AuthenticatorRecoveryCodeHashes = remainingHashes;
            return true;
        }

        if (!_authenticatorService.IsConfigured ||
            string.IsNullOrWhiteSpace(user.AuthenticatorSecretEncrypted))
            return false;

        try
        {
            var secret = _authenticatorService.DecryptSecret(user.AuthenticatorSecretEncrypted);
            return _authenticatorService.VerifyCode(secret, code);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Authenticator secret decryption failed for user {UserId}", user.Id);
            return false;
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Authenticator secret format was invalid for user {UserId}", user.Id);
            return false;
        }
    }

    private static int ReadRecoveryCodeCount(string? serializedHashes)
    {
        if (string.IsNullOrWhiteSpace(serializedHashes)) return 0;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(serializedHashes)?.Count ?? 0; }
        catch (System.Text.Json.JsonException) { return 0; }
    }

    private AuthResponse CreateSessionResponse(User user, string message) => new()
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
            ProfileCompleted = _userPolicyService.IsProfileComplete(user),
            ProfileCompletion = _userPolicyService.GetProfileCompletion(user),
            MissingProfileFields = _userPolicyService.GetMissingProfileFields(user).ToArray(),
            CanCreateTasks = _userPolicyService.CanCreateTasks(user),
            CanAcceptTasks = _userPolicyService.CanAcceptTasks(user),
            Rating = user.Rating,
            CompletedTasks = user.CompletedTasks,
            Roles = user.Roles,
            IsAdmin = user.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(role => string.Equals(role.Trim(), "Admin", StringComparison.OrdinalIgnoreCase)) == true,
            UserType = user.UserType,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            IsVerified = user.IsVerified,
            EmailVerified = user.EmailVerified,
            PhoneVerified = user.PhoneVerified,
            IdentityVerificationStatus = user.IdentityVerificationStatus,
            AuthenticatorTwoFactorEnabled = user.AuthenticatorTwoFactorEnabled,
            HasGoogleLogin = !string.IsNullOrWhiteSpace(user.GoogleSubject)
        },
        Message = message
    };

    private int? GetCurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(value, out var userId) ? userId : null;
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

    private System.Threading.Tasks.Task SendVerificationEmailAsync(string email, string token, CancellationToken cancellationToken)
    {
        var link = $"{GetPublicFrontendUrl()}/verify-email?token={Uri.EscapeDataString(token)}";
        var body = $"Welcome to DoForYou.\n\nVerify your email address using this link (valid for 24 hours):\n{link}\n\nIf you did not create this account, you can ignore this email.";
        return _emailSender.SendAsync(email, "Verify your DoForYou email", body, cancellationToken);
    }

    private System.Threading.Tasks.Task SendPasswordResetEmailAsync(string email, string token, CancellationToken cancellationToken)
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