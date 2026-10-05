namespace DoForYou.API.Models;

public class Payout
{
    public int Id { get; set; }

    public int TaskId { get; set; }
    public int RunnerId { get; set; }
    public int BankAccountId { get; set; }

    public decimal Amount { get; set; }

    public string Status { get; set; } = "NotStarted";
    public string Provider { get; set; } = "Paystack";

    public string MerchantReference { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }
    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessingAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? ProcessingError { get; set; }

    public Task Task { get; set; } = null!;
    public User Runner { get; set; } = null!;
    public BankAccount BankAccount { get; set; } = null!;
}
