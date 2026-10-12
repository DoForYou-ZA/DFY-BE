using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
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

    public AuthController(AppDbContext context, IConfiguration configuration, ILogger<AuthController> logger, INotificationService notificationService, IUserPolicyService userPolicyService)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
        _notificationService = notificationService;
        _userPolicyService = userPolicyService;
    }

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request)
    {
        if (await _context.Users.AnyAsync(u => u.Email == request.Email))
        {
            return Ok(new AuthResponse
            {
                Success = false,
                Message = "Email already exists"
            });
        }

        var user = new User
        {
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email ?? string.Empty,
            PhoneNumber = request.PhoneNumber,
            UserType = request.UserType,
            IdNumber = request.IdNumber,
            Address = request.Address,
            DateOfBirth = ConvertToUtc(request.DateOfBirth),
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsVerified = false,
            ProfileCompleted = false,
            EmailVerified = false,
            PhoneVerified = false,
            Roles = "User",
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

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

        var token = GenerateJwtToken(user);

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
            Message = "Registration successful"
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

        if (string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
            return Ok(new ApiResponse<bool> { Success = false, Message = "New password must be different from the current password" });

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return Ok(new ApiResponse<bool> { Success = false, Message = "Current password is incorrect" });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Password changed successfully" });
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request)
    {
        if (request == null)
            return BadRequest("Invalid request");
        
        // Clean email - remove mailto: prefix if present
        var cleanEmail = request.Email?.Replace("mailto:", "").Trim();
        
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == cleanEmail);
        
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized(new AuthResponse
            {
                Success = false,
                Message = "Invalid email or password"
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
            new Claim(ClaimTypes.Email, user.Email)
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