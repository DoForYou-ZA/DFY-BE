namespace DoForYou.API.Models;

public class Refund
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string RefundId { get; set; } = string.Empty;
    public string TransactionId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending"; // Pending, Submitted, Complete, Failed, Cancelled, Returned
    public string? Provider { get; set; } = "Paystack";
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastReconciledAt { get; set; }

    public Task Task { get; set; } = null!;
}
