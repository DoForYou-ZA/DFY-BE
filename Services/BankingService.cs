using DoForYou.API.Models;
using DoForYou.API.Data;
using Microsoft.EntityFrameworkCore;
using Task = System.Threading.Tasks.Task;

namespace DoForYou.API.Services;

public record BankVerificationResult(
    bool Success,
    string Message,
    string? ProviderReference = null);

public interface IBankingService
{
    Task<bool> ValidateBankAccountAsync(
        string bankName,
        string accountNumber,
        string branchCode);

    Task<BankVerificationResult> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default);
}

public sealed class BankingService(
    IBankProvider bankProvider,
    AppDbContext context) : IBankingService
{
    public Task<bool> ValidateBankAccountAsync(
        string bankName,
        string accountNumber,
        string branchCode)
    {
        var valid = !string.IsNullOrWhiteSpace(bankName) &&
                    !string.IsNullOrWhiteSpace(accountNumber) &&
                    accountNumber.Length is >= 9 and <= 11 &&
                    accountNumber.All(char.IsDigit) &&
                    !string.IsNullOrWhiteSpace(branchCode) &&
                    branchCode.Length == 6 &&
                    branchCode.All(char.IsDigit);

        return Task.FromResult(valid);
    }

    public async Task<BankVerificationResult> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default)
    {
        var result = await bankProvider.VerifyBankAccountAsync(
            user,
            bankAccount,
            cancellationToken);

        if (result.Success && !string.IsNullOrWhiteSpace(result.ProviderReference))
        {
            var payouts = await context.Payouts
                .Include(p => p.Task)
                .Where(p =>
                    p.BankAccountId == bankAccount.Id &&
                    (p.Status == "Failed" || p.Status == "AwaitingBankDetails") &&
                    p.FailureReason == "Runner bank account is missing its Paystack recipient reference.")
                .ToListAsync(cancellationToken);

            foreach (var payout in payouts)
            {
                payout.Status = "Pending";
                payout.FailureReason = null;
                payout.ProcessingError = null;
                payout.NextAttemptAt = null;
                payout.UpdatedAt = DateTime.UtcNow;
                payout.Task.PayoutStatus = "Pending";
                payout.Task.TaskStatus = "PayoutPending";
                payout.Task.UpdatedAt = DateTime.UtcNow;
            }
        }

        return new BankVerificationResult(
            result.Success,
            result.Message,
            result.ProviderReference);
    }
}
