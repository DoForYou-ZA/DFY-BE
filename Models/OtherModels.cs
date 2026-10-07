namespace DoForYou.API.Models;

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Payment
{
    public int Id { get; set; }
    public string PaymentId { get; set; } = string.Empty;
    public int TaskId { get; set; }
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public string Provider { get; set; } = "Paystack";
    public string Status { get; set; } = string.Empty;
    public string? PaymentUrl { get; set; }
    public string? TransactionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public Task Task { get; set; } = null!;
}

public class PaymentAttempt
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string Provider { get; set; } = "Paystack";
    public string Reference { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Pending";
    public string? PaymentUrl { get; set; }
    public string? TransactionId { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public Task Task { get; set; } = null!;
}

public class TaskMessage
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int SenderId { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User Sender { get; set; } = null!;
}

public class TaskProgressUpdate
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int UserId { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}

public class Rating
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int RatedByUserId { get; set; }
    public int RatedUserId { get; set; }
    public int RatingValue { get; set; }
    public string? Review { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Task Task { get; set; } = null!;
    public User RatedByUser { get; set; } = null!;
    public User RatedUser { get; set; } = null!;
}

public class Dispute
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int ReportedByUserId { get; set; }
    public string Issue { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Resolution { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    public Task Task { get; set; } = null!;
    public User ReportedByUser { get; set; } = null!;
}

public class WalletTransaction
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public decimal Amount { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Reference { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public int? RelatedTaskId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public Models.Task? RelatedTask { get; set; }
}

public class BankAccount
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string BankName { get; set; } = string.Empty;
    public string? BankGroupId { get; set; } // Legacy Ozow field; retained for migration compatibility.
    public string? BankCode { get; set; }
    public string? ProviderReference { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountHolderName { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? VerifiedAt { get; set; }
    public string? VerificationMessage { get; set; }

    public User User { get; set; } = null!;
}

public class WithdrawalRequest
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int BankAccountId { get; set; }
    public decimal Amount { get; set; }
    public decimal Fee { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Processing, Completed, Failed, Cancelled
    public string? Reference { get; set; }
    public string? FailureReason { get; set; }
    public string? OtpCode { get; set; }
    public DateTime? OtpExpiresAt { get; set; }
    public bool OtpVerified { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public User User { get; set; } = null!;
    public BankAccount BankAccount { get; set; } = null!;
}

public class AuditLog
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public int? EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}

public class SupportTicket
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public string Priority { get; set; } = "Normal";
    public string Status { get; set; } = "Open";
    public string? AssignedTo { get; set; }
    public string? AdminNotes { get; set; }
    public string Reference { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    public User? User { get; set; }
}