using DoForYou.API.Models;

namespace DoForYou.API.Services;

public record BankVerificationResult(
    bool Success,
    string Message,
    string? ProviderReference = null);

public interface IBankingService
{
    Task<bool> ValidateBankAccountAsync(string bankName, string accountNumber, string branchCode);
    Task<BankVerificationResult> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default);
}

public sealed class BankingService(
    IBankProvider bankProvider,
    ILogger<BankingService> logger) : IBankingService
{
    public Task<bool> ValidateBankAccountAsync(
        string bankName,
        string accountNumber,
        string branchCode)
    {
        var valid = !string.IsNullOrWhiteSpace(bankName) &&
                    !string.IsNullOrWhiteSpace(accountNumber) &&
                    accountNumber.All(char.IsDigit) &&
                    accountNumber.Length is >= 7 and <= 12 &&
                    !string.IsNullOrWhiteSpace(branchCode) &&
                    branchCode.All(char.IsDigit) &&
                    branchCode.Length == 6;

        return Task.FromResult(valid);
    }

    public async Task<BankVerificationResult> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await bankProvider.VerifyBankAccountAsync(
                user,
                bankAccount,
                cancellationToken);

            return new BankVerificationResult(
                result.Success,
                result.Message,
                result.ProviderReference);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Bank account verification failed through {Provider} for bank account {BankAccountId}",
                bankProvider.Name,
                bankAccount.Id);

            return new BankVerificationResult(
                false,
                "Bank verification is temporarily unavailable.");
        }
    }
}
