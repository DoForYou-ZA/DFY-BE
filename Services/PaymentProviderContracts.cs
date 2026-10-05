using DoForYou.API.Models;

namespace DoForYou.API.Services;

public record PaymentCreationResult(
    bool Success,
    string? PaymentUrl,
    string? TransactionReference,
    string? Error);

public record PaymentVerificationResult(
    bool Success,
    bool Found,
    string? TransactionId,
    string? Status,
    decimal? Amount,
    string? TransactionReference,
    string? Error);

public record RefundResult(
    bool Success,
    string? RefundId,
    string? TransactionId,
    decimal? Amount,
    string? Status,
    string? Error);

public record PayoutSubmissionResult(
    bool Success,
    string? ProviderReference,
    bool Retryable,
    string? Error);

public record PayoutStatusResult(
    bool Success,
    bool Found,
    string? ProviderReference,
    string? Status,
    string? Error);

public record SupportedBank(
    string Code,
    string Name,
    string? BranchCode = null);

public record BankVerificationResultV2(
    bool Success,
    string Message,
    string? ProviderReference = null);

public interface IPaymentProvider
{
    Task<PaymentCreationResult> CreatePaymentAsync(
        Models.Task task,
        User customer,
        CancellationToken cancellationToken = default);

    Task<PaymentVerificationResult> VerifyPaymentAsync(
        string transactionReference,
        CancellationToken cancellationToken = default);

    Task<RefundResult> CreateRefundAsync(
        string transactionId,
        decimal amount,
        string reason,
        CancellationToken cancellationToken = default);

    bool VerifyWebhookSignature(string rawBody, string? signature);
}

public interface IPayoutProvider
{
    Task<PayoutSubmissionResult> CreatePayoutAsync(
        Payout payout,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default);

    Task<PayoutStatusResult> GetPayoutStatusAsync(
        Payout payout,
        CancellationToken cancellationToken = default);
}

public interface IBankProvider
{
    Task<IReadOnlyList<SupportedBank>> GetSupportedBanksAsync(
        CancellationToken cancellationToken = default);

    Task<BankVerificationResultV2> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default);
}
