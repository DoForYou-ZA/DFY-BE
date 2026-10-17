using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/user")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class UserController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IUserPolicyService _userPolicyService;

    public UserController(AppDbContext context, IUserPolicyService userPolicyService)
    {
        _context = context;
        _userPolicyService = userPolicyService;
    }

    [HttpGet("profile")]
    public async Task<ActionResult<ApiResponse<object>>> GetProfile()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();

        var hasVerifiedBank = await _context.BankAccounts.AnyAsync(b =>
            b.UserId == userId.Value &&
            b.IsActive &&
            b.IsVerified &&
            b.ProviderReference != null &&
            b.ProviderReference != "");

        return Ok(new ApiResponse<object> {
            Success = true,
            Data = new {
                id = user.Id, firstName = user.FirstName, lastName = user.LastName, email = user.Email,
                phoneNumber = user.PhoneNumber, address = user.Address, userType = user.UserType,
                hasIdNumber = !string.IsNullOrWhiteSpace(user.IdNumber),
                idNumberLastFour = user.IdNumber != null && user.IdNumber.Length >= 4 ? user.IdNumber[^4..] : null,
                username = user.Username,
                profileCompleted = _userPolicyService.IsProfileComplete(user),
                profileCompletion = _userPolicyService.GetProfileCompletion(user),
                missingProfileFields = _userPolicyService.GetMissingProfileFields(user),
                canCreateTasks = _userPolicyService.CanCreateTasks(user),
                canAcceptTasks = _userPolicyService.CanAcceptTasks(user),
                identityVerificationStatus = user.IdentityVerificationStatus,
                authenticatorTwoFactorEnabled = user.AuthenticatorTwoFactorEnabled,
                hasVerifiedPayoutAccount = hasVerifiedBank,
                hasGoogleLogin = !string.IsNullOrWhiteSpace(user.GoogleSubject),
                hasPasswordLogin = user.PasswordLoginEnabled,
                marketingConsent = user.MarketingConsent,
                marketingConsentUpdatedAt = user.MarketingConsentUpdatedAt,
                marketingConsentVersion = user.MarketingConsentVersion,
                rating = user.Rating, completedTasks = user.CompletedTasks, isVerified = user.IsVerified,
                emailVerified = user.EmailVerified, phoneVerified = user.PhoneVerified,
                isAvailable = user.IsAvailable,
                bio = user.Bio, serviceCategories = user.ServiceCategories, coverageArea = user.CoverageArea,
                profilePhotoUrl = user.ProfilePhotoUrl
            }
        });
    }

    [HttpPost("profile")]
    [HttpPut("profile")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "First name and last name are required." });

        string? normalizedPhone = null;
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber))
        {
            normalizedPhone = NormalizeSouthAfricanPhone(request.PhoneNumber);
            if (normalizedPhone == null)
                return BadRequest(new ApiResponse<bool> { Success = false, Message = "Enter a valid South African phone number." });
        }

        var requestedUserType = string.IsNullOrWhiteSpace(request.UserType)
            ? user.UserType.Trim().ToLowerInvariant()
            : request.UserType.Trim().ToLowerInvariant();
        if (requestedUserType is not ("creator" or "runner" or "both"))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Invalid user type." });
        // Full IDs and DOB are high-risk data. Do not accept new identity data through
        // the general profile API; a dedicated provider-backed and protected runner
        // verification flow must own collection, consent, retention and access controls.
        if (!string.IsNullOrWhiteSpace(request.IdNumber) || request.DateOfBirth.HasValue)
            return Conflict(new ApiResponse<bool>
            {
                Success = false,
                Message = "Identity details can only be updated through the dedicated runner verification flow, which is not enabled on this staging release."
            });

        if (!string.IsNullOrWhiteSpace(request.Address) && _userPolicyService.IsPlaceholderAddress(request.Address))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Please enter a real area or suburb, not a placeholder address." });

        user.FirstName = request.FirstName.Trim();
        user.LastName = request.LastName.Trim();
        if (normalizedPhone != null && !string.Equals(user.PhoneNumber, normalizedPhone, StringComparison.Ordinal))
        {
            user.PhoneNumber = normalizedPhone;
            // A changed contact number cannot inherit an earlier phone-verification state.
            user.PhoneVerified = false;
        }
        if (!string.IsNullOrWhiteSpace(request.Address)) user.Address = request.Address.Trim();
        if (!string.IsNullOrWhiteSpace(request.Username)) user.Username = request.Username.Trim();

        if (!string.IsNullOrWhiteSpace(request.UserType))
            user.UserType = requestedUserType;

        user.ProfileCompleted = _userPolicyService.IsProfileComplete(user);
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Profile updated successfully" });
    }

    [HttpPut("marketing-consent")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateMarketingConsent([FromBody] MarketingConsentRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null || user.IsArchived) return Unauthorized();

        const string currentMarketingVersion = "marketing-v1";
        var enabled = request.Enabled;
        if (user.MarketingConsent == enabled &&
            string.Equals(user.MarketingConsentVersion, currentMarketingVersion, StringComparison.Ordinal))
        {
            return Ok(new ApiResponse<object>
            {
                Success = true,
                Data = new { enabled = user.MarketingConsent, version = user.MarketingConsentVersion, updatedAt = user.MarketingConsentUpdatedAt },
                Message = "Marketing preference is already up to date."
            });
        }

        var now = DateTime.UtcNow;
        user.MarketingConsent = enabled;
        user.MarketingConsentUpdatedAt = now;
        user.MarketingConsentVersion = currentMarketingVersion;
        _context.UserConsentEvents.Add(new UserConsentEvent
        {
            UserId = user.Id,
            ConsentType = "marketing",
            Accepted = enabled,
            Version = currentMarketingVersion,
            RecordedAt = now,
            Source = "account-settings"
        });
        await _context.SaveChangesAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { enabled = user.MarketingConsent, version = user.MarketingConsentVersion, updatedAt = user.MarketingConsentUpdatedAt },
            Message = enabled
                ? "Optional marketing updates are enabled."
                : "Optional marketing updates have been turned off."
        });
    }

    [HttpGet("dashboard/stats")]
    public async Task<ActionResult<ApiResponse<object>>> GetDashboardStats()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var postedTasks = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId && !t.IsDeleted);
        var activeTasks = await _context.Tasks.CountAsync(t => t.AcceptedByUserId == userId && !t.IsDeleted && t.TaskStatus == "Claimed");
        var completedTasks = await _context.Tasks.CountAsync(t => !t.IsDeleted && (t.CreatedByUserId == userId || t.AcceptedByUserId == userId) && t.TaskStatus == "Completed");
        var totalEarnings = await _context.Tasks.Where(t => t.AcceptedByUserId == userId && !t.IsDeleted && t.TaskStatus == "Completed").SumAsync(t => t.Budget);
        return Ok(new ApiResponse<object> { Success = true, Data = new { postedTasks, activeTasks, completedTasks, totalEarnings } });
    }

    [HttpPost("availability")]
    public async Task<ActionResult<ApiResponse<bool>>> SetAvailability([FromBody] AvailabilityRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();
        user.IsAvailable = request.IsAvailable;
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = request.IsAvailable, Message = "Availability updated." });
    }

    [HttpPost("validate-id")]
    public ActionResult<ApiResponse<object>> ValidateId([FromBody] ValidateIdRequest request)
    {
        var valid = _userPolicyService.TryGetAdultDateOfBirthFromSouthAfricanId(request.IdNumber, out var dateOfBirth);
        return Ok(new ApiResponse<object>
        {
            Success = valid,
            Data = new { valid, dateOfBirth = valid ? dateOfBirth : (DateTime?)null },
            Message = valid ? "Valid adult ID number" : "Enter a valid 13-digit South African ID number for an adult aged 18 or older."
        });
    }

    [HttpGet("preferences")]
    public async Task<ActionResult<ApiResponse<object>>> GetUserPreferences()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        return Ok(new ApiResponse<object> {
            Success = true,
            Data = new {
                canCreateTasks = _userPolicyService.CanCreateTasks(user!),
                canAcceptTasks = _userPolicyService.CanAcceptTasks(user!),
                taskCreatorNotifications = true, taskRunnerNotifications = true, paymentNotifications = true,
                emailNotifications = true, smsNotifications = false, minTaskAmount = 50, maxTaskAmount = 100000,
                preferredCategories = Array.Empty<string>(), preferredLocations = Array.Empty<string>()
            }
        });
    }

    [HttpPut("preferences")]
    [HttpPost("preferences")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserPreferences([FromBody] UserPreferencesRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound();

        var type = request.UserType?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(type))
            type = request.CanCreateTasks && request.CanAcceptTasks ? "both" :
                   request.CanCreateTasks ? "creator" :
                   request.CanAcceptTasks ? "runner" : null;

        if (type is not ("creator" or "runner" or "both"))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Select Creator, Runner, or Both." });

        user.UserType = type;
        user.ProfileCompleted = _userPolicyService.IsProfileComplete(user);
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Preferences updated successfully" });
    }

    private static string? NormalizeSouthAfricanPhone(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith("27", StringComparison.Ordinal))
            digits = "0" + digits[2..];
        else if (digits.Length == 9 && digits[0] is >= '6' and <= '8')
            digits = "0" + digits;
        return digits.Length == 10 && digits[0] == '0' && digits[1] is >= '6' and <= '8'
            ? digits
            : null;
    }

    private int? GetCurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }

}

public sealed class MarketingConsentRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    public bool Enabled { get; set; }
}

public class UpdateProfileRequest
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? Address { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? IdNumber { get; set; }
    public string? Username { get; set; }
    public string? UserType { get; set; }
}

public class UserPreferencesRequest
{
    public bool CanCreateTasks { get; set; }
    public bool CanAcceptTasks { get; set; }
    public string? UserType { get; set; }
}
