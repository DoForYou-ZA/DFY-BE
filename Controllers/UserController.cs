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
                hasIdNumber = !string.IsNullOrWhiteSpace(user.IdNumber), idNumberLastFour = user.IdNumber != null && user.IdNumber.Length >= 4 ? user.IdNumber[^4..] : null, dateOfBirth = user.DateOfBirth, username = user.Username,
                profileCompleted = _userPolicyService.IsProfileComplete(user, hasVerifiedBank),
                profileCompletion = _userPolicyService.GetProfileCompletion(user, hasVerifiedBank),
                missingProfileFields = _userPolicyService.GetMissingProfileFields(user, hasVerifiedBank),
                canCreateTasks = _userPolicyService.CanCreateTasks(user),
                canAcceptTasks = _userPolicyService.CanAcceptTasks(user, hasVerifiedBank),
                rating = user.Rating, completedTasks = user.CompletedTasks, isVerified = user.IsVerified,
                emailVerified = user.EmailVerified, phoneVerified = user.PhoneVerified, isAvailable = user.IsAvailable,
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

        if (!string.IsNullOrWhiteSpace(request.PhoneNumber) && !_userPolicyService.IsValidPhone(request.PhoneNumber))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Enter a valid South African phone number." });

        if (!string.IsNullOrWhiteSpace(request.Address) && _userPolicyService.IsPlaceholderAddress(request.Address))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Please enter a real area or suburb, not a placeholder address." });

        if (!string.IsNullOrWhiteSpace(request.IdNumber))
        {
            var id = request.IdNumber.Trim();
            if (!_userPolicyService.TryGetAdultDateOfBirthFromSouthAfricanId(id, out var derivedDob))
                return BadRequest(new ApiResponse<bool> { Success = false, Message = "Enter a valid 13-digit South African ID number for an adult aged 18 or older." });
            if (await _context.Users.AnyAsync(u => u.Id != user.Id && u.IdNumber == id))
                return Conflict(new ApiResponse<bool> { Success = false, Message = "We couldn't update these details. Contact support if you believe this is an error." });
            user.IdNumber = id;
            user.DateOfBirth = derivedDob;
        }
        else if (request.DateOfBirth.HasValue && _userPolicyService.TryGetAdultDateOfBirthFromSouthAfricanId(user.IdNumber, out var existingDob))
        {
            if (request.DateOfBirth.Value.Date != existingDob.Date)
                return BadRequest(new ApiResponse<bool> { Success = false, Message = "Date of birth must match the date encoded in your ID number." });
            user.DateOfBirth = existingDob;
        }

        user.FirstName = request.FirstName.Trim();
        user.LastName = request.LastName.Trim();
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber)) user.PhoneNumber = request.PhoneNumber.Trim();
        if (!string.IsNullOrWhiteSpace(request.Address)) user.Address = request.Address.Trim();
        if (!string.IsNullOrWhiteSpace(request.Username)) user.Username = request.Username.Trim();

        if (!string.IsNullOrWhiteSpace(request.UserType))
        {
            var type = request.UserType.Trim().ToLowerInvariant();
            if (type is not ("creator" or "runner" or "both"))
                return BadRequest(new ApiResponse<bool> { Success = false, Message = "Invalid user type." });
            user.UserType = type;
        }

        user.ProfileCompleted = _userPolicyService.IsProfileComplete(user);
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Profile updated successfully" });
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
        var valid = _userPolicyService.IsValidSouthAfricanId(request.IdNumber);
        return Ok(new ApiResponse<object> { Success = valid, Data = new { valid }, Message = valid ? "Valid ID number" : "Invalid ID number" });
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

    private int? GetCurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(value, out var id) ? id : null;
    }

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
