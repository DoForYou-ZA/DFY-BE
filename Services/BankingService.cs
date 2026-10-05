using DoForYou.API.Models;

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

public sealed class BankingService(IBankProvider bankProvider) : IBankingService
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

        return new BankVerificationResult(
            result.Success,
            result.Message,
            result.ProviderReference);
    }
}
