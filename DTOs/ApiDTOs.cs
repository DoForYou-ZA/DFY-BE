using System.ComponentModel.DataAnnotations;

namespace DoForYou.API.DTOs;

public class CreateTaskRequest : IValidatableObject
{
    [Required]
    [StringLength(60)]
    public string TaskName { get; set; } = string.Empty;

    [Required]
    [StringLength(500, MinimumLength = 20, ErrorMessage = "Task description must be between 20 and 500 characters.")]
    public string TaskDescription { get; set; } = string.Empty;

    [Required]
    public string Category { get; set; } = string.Empty;

    [Required]
    public string Area { get; set; } = string.Empty;

    [Required]
    public DateTime DateNeeded { get; set; }

    [Required]
    [Range(50, 100000, ErrorMessage = "Budget must be between R50 and R100000.")]
    public decimal Budget { get; set; }

    public string? Notes { get; set; }
    public string Priority { get; set; } = "Standard";
    public bool TermsAccepted { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var errors = new List<ValidationResult>();

        if (string.IsNullOrWhiteSpace(TaskName))
        {
            errors.Add(new ValidationResult("Task name is required."));
        }
        else if (TaskName.Trim().Length > 60)
        {
            errors.Add(new ValidationResult("Task name must be 60 characters or less."));
        }

        if (string.IsNullOrWhiteSpace(TaskDescription))
        {
            errors.Add(new ValidationResult("Task description is required."));
        }
        else if (TaskDescription.Trim().Length < 20 || TaskDescription.Trim().Length > 500)
        {
            errors.Add(new ValidationResult("Task description must be between 20 and 500 characters."));
        }

        var category = Category?.Trim();
        if (string.IsNullOrWhiteSpace(category))
        {
            errors.Add(new ValidationResult("Category is required."));
        }

        var area = Area?.Trim();
        if (string.IsNullOrWhiteSpace(area))
        {
            errors.Add(new ValidationResult("Area is required."));
        }
        else if (area.Length < 2)
        {
            errors.Add(new ValidationResult("Area must be at least 2 characters long."));
        }

        var normalizedPriority = Priority?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedPriority) && !new[] { "Low", "Medium", "High", "Urgent", "Standard" }.Contains(normalizedPriority, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new ValidationResult("Priority is invalid."));
        }

        if (DateNeeded <= DateTime.UtcNow.AddMinutes(30))
        {
            errors.Add(new ValidationResult("Date needed must be in the future."));
        }

        if (DateNeeded > DateTime.UtcNow.AddDays(365))
        {
            errors.Add(new ValidationResult("Date needed cannot be more than 365 days in the future."));
        }

        if (Budget < 50 || Budget > 100000)
        {
            errors.Add(new ValidationResult("Budget must be between R50 and R100000."));
        }

        if (!string.IsNullOrWhiteSpace(Notes) && Notes.Trim().Length > 1000)
        {
            errors.Add(new ValidationResult("Notes must be 1000 characters or less."));
        }

        if (!TermsAccepted)
        {
            errors.Add(new ValidationResult("You must accept the terms and conditions before posting a task."));
        }

        if ((Latitude.HasValue && !Longitude.HasValue) || (!Latitude.HasValue && Longitude.HasValue))
        {
            errors.Add(new ValidationResult("Both latitude and longitude must be provided together."));
        }

        return errors;
    }
}

public class UpdateTaskRequest
{
    [StringLength(60, MinimumLength = 1)]
    public string? TaskName { get; set; }
    public string? TaskDescription { get; set;}
    public string? Category { get; set; }
    public string? Area { get; set; }
    public DateTime? DateNeeded { get; set; }
    public decimal? Budget { get; set; }
    public string? Notes { get; set; }
    public string? Priority { get; set; }
}

public class TaskDto
{
    public int Id { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string UserContact { get; set; } = string.Empty;
    public int CreatedByUserId { get; set; }
    public string TaskDescription { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public DateTime DateNeeded { get; set; }
    public decimal Budget { get; set; }
    public decimal PayoutAmount { get; set; }
    public string? Notes { get; set; }
    public string PaymentStatus { get; set; } = string.Empty;
    public string TaskStatus { get; set; } = string.Empty;
    public string? HelperName { get; set; }
    public string? HelperContact { get; set; }
    public string Priority { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? EscrowHoldUntil { get; set; }
    public string PayoutStatus { get; set; } = string.Empty;
    public bool CanAccept { get; set; } = true;
    
    // Additional fields for frontend compatibility
    public string Timestamp => CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    public string Name => UserName; // Legacy compatibility
    public string Contact => UserContact; // Legacy compatibility
    public string Status => TaskStatus; // Legacy compatibility
}

public class ClaimTaskRequest
{
    [Required]
    public string HelperName { get; set; } = string.Empty;

    [Required]
    public string HelperContact { get; set; } = string.Empty;

    [Required]
    public bool TermsAccepted { get; set; }
}

public class UpdatePaymentStatusRequest
{
    [Required]
    public string PaymentStatus { get; set; } = string.Empty;
}

public class ApiResponse<T>
{
    public bool Success { get; set; } = true;
    public T? Data { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public class PaginatedResponse<T>
{
    public bool Success { get; set; } = true;
    public int Count { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public List<T> Tasks { get; set; } = new();
    public Dictionary<string, int>? StatusCounts { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
    
    [Required]
    public string Password { get; set; } = string.Empty;
}

public class RegisterRequest
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string FirstName { get; set; } = string.Empty;

    [Required, StringLength(50, MinimumLength = 1)]
    public string LastName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(254)]
    public string Email { get; set; } = string.Empty;

    // Passwords are deliberately not trimmed or normalized.
    [Required, StringLength(72, MinimumLength = 12)]
    public string Password { get; set; } = string.Empty;

    [Required, StringLength(20)]
    public string PhoneNumber { get; set; } = string.Empty;

    [Required, StringLength(10)]
    public string UserType { get; set; } = string.Empty;

    [Required, RegularExpression("^[0-9]{13}$")]
    public string IdNumber { get; set; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 2)]
    public string Address { get; set; } = string.Empty;

    // Kept for client compatibility only; the server derives DOB from ID.
    public DateTime? DateOfBirth { get; set; }

    [StringLength(80)]
    public string? Username { get; set; }

    [Required]
    public bool TermsAccepted { get; set; }

    [Required]
    public bool PrivacyAccepted { get; set; }

    [Required, StringLength(40)]
    public string TermsVersion { get; set; } = string.Empty;

    [Required, StringLength(40)]
    public string PrivacyVersion { get; set; } = string.Empty;

    public bool MarketingConsent { get; set; }
}

public class AuthResponse
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public UserDto? User { get; set; }
    public string? Message { get; set; }
}

public class UserDto
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Name => $"{FirstName} {LastName}"; // Computed field for frontend
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string Contact => PhoneNumber ?? Email; // Computed field for frontend
    public bool ProfileCompleted { get; set; }
    public int ProfileCompletion { get; set; }
    public string[] MissingProfileFields { get; set; } = Array.Empty<string>();
    public bool CanCreateTasks { get; set; }
    public bool CanAcceptTasks { get; set; }
    public decimal Rating { get; set; }
    public int CompletedTasks { get; set; }
    public string? Roles { get; set; }
    public string[] RolesArray => Roles?.Split(',') ?? new string[0]; // For frontend compatibility
    public bool IsAdmin { get; set; }
    public string? UserType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public bool IsVerified { get; set; }
}


public class WithdrawalRequestDto
{
    public decimal Amount { get; set; }
    public string? BankAccount { get; set; }
    public string? BankName { get; set; }
    public string? AccountHolder { get; set; }
    public string? BranchCode { get; set; }
    public string? AccountType { get; set; }
}

public class RaiseDisputeRequest
{
    [Required]
    public string TaskId { get; set; } = string.Empty;
    [Required]
    public string Issue { get; set; } = string.Empty;
    [Required]
    public string Category { get; set; } = string.Empty;
}

public class ResolveDisputeRequest
{
    [Required]
    public string Resolution { get; set; } = string.Empty;
    public string? Action { get; set; }
}

public class SubmitRatingRequest
{
    [Required]
    public string TaskId { get; set; } = string.Empty;
    [Required, Range(1, 5)]
    public int RatingValue { get; set; }
    public string? Review { get; set; }
}

public class UpdateUserStatusRequest
{
    public bool IsVerified { get; set; }

    [Required, StringLength(500, MinimumLength = 5)]
    public string Reason { get; set; } = string.Empty;
}

public class AdminActionReasonRequest
{
    [Required, StringLength(500, MinimumLength = 5)]
    public string Reason { get; set; } = string.Empty;
}

public class UpdateUserRoleRequest
{
    [Required]
    public string Role { get; set; } = string.Empty;
}

public class BulkVerifyRequest
{
    [Required]
    public List<string> TaskIds { get; set; } = new();

    [Required, StringLength(500, MinimumLength = 5)]
    public string Reason { get; set; } = string.Empty;
}

public class CancelTaskRequest
{
    public string? Reason { get; set; }
}

public class InitiatePaymentRequest
{
    [Required]
    public string TaskId { get; set; } = string.Empty;
}

public class CreateSupportTicketRequest
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    [System.ComponentModel.DataAnnotations.Required]
    public string Subject { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.Required]
    public string Message { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Priority { get; set; }
}

public class UpdateSupportTicketRequest
{
    public string? Status { get; set; }
    public string? AssignedTo { get; set; }
    public string? AdminNotes { get; set; }
}

public class CategoryRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public int? SortOrder { get; set; }
}

public class ReorderRequest
{
    public int SortOrder { get; set; }
}

public class AvailabilityRequest
{
    public bool IsAvailable { get; set; }
}

public class ProviderProfileRequest
{
    public string? Bio { get; set; }
    public string? ServiceCategories { get; set; }
    public string? CoverageArea { get; set; }
}

public class VerifyPhoneRequest
{
    [System.ComponentModel.DataAnnotations.Required]
    public string Code { get; set; } = string.Empty;
}

public class SendVerificationRequest
{
    public string? Type { get; set; } = "phone"; // "phone" or "email"
}

public class ValidateIdRequest
{
    public string? IdNumber { get; set; }
}

public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;
    [Required, MinLength(6)]
    public string NewPassword { get; set; } = string.Empty;
}

public class VerifyOtpRequest
{
    [Required]
    public string Reference { get; set; } = string.Empty;
    [Required]
    public string OtpCode { get; set; } = string.Empty;
}
