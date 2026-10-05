using System.Globalization;
using System.Text.Json;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

public record SupportedBank(string Code, string Name, string UniversalBranchCode, bool SupportsVerification);
public record ProviderBankVerification(
    bool Success,
    string Message,
    string? ProviderReference = null);

public interface IBankProvider
{
    string Name { get; }
    Task<IReadOnlyList<SupportedBank>> GetSupportedBanksAsync(CancellationToken cancellationToken = default);
    Task<ProviderBankVerification> VerifyBankAccountAsync(User user, BankAccount bankAccount, CancellationToken cancellationToken = default);
}

public sealed class PaystackBankProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<PaystackBankProvider> logger) : IBankProvider
{
    public string Name => "Paystack";

    private HttpClient Client => httpClientFactory.CreateClient("Paystack");

    private string? SecretKey =>
        Environment.GetEnvironmentVariable("PAYSTACK_SECRET_KEY")
        ?? configuration["Paystack:SecretKey"];

    private string BaseUrl =>
        (Environment.GetEnvironmentVariable("PAYSTACK_BASE_URL")
         ?? configuration["Paystack:BaseUrl"]
         ?? "https://api.paystack.co").TrimEnd('/');

    public async Task<IReadOnlyList<SupportedBank>> GetSupportedBanksAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return Array.Empty<SupportedBank>();

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl}/bank?currency=ZAR&enabled_for_verification=true&perPage=100");
            AddAuth(request);

            using var response = await Client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<SupportedBank>();

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return Array.Empty<SupportedBank>();

            return data.EnumerateArray()
                .Where(x => x.TryGetProperty("active", out var active) && active.GetBoolean())
                .Select(x =>
                {
                    var code = ReadString(x, "code") ?? string.Empty;
                    var name = ReadString(x, "name") ?? string.Empty;
                    var branch = ReadString(x, "longcode") ?? code;
                    return new SupportedBank(code, name, branch, true);
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Code) && !string.IsNullOrWhiteSpace(x.Name))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack bank list request failed.");
            return Array.Empty<SupportedBank>();
        }
    }

    public async Task<ProviderBankVerification> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, "Paystack is not configured.");

        if (string.IsNullOrWhiteSpace(user.IdNumber))
            return new(false, "Your South African ID number is required before the bank account can be verified.");

        if (string.IsNullOrWhiteSpace(bankAccount.BankGroupId))
            return new(false, "The selected bank is invalid.");

        var payload = new
        {
            bank_code = bankAccount.BankGroupId,
            country_code = "ZA",
            account_number = bankAccount.AccountNumber,
            account_name = bankAccount.AccountHolderName,
            account_type = "personal",
            document_type = "identityNumber",
            document_number = user.IdNumber
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/bank/validate");
            AddAuth(request);
            request.Content = JsonContent.Create(payload);

            using var response = await Client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode ||
                !root.TryGetProperty("status", out var status) ||
                !status.GetBoolean())
                return new(false, ReadString(root, "message") ?? "Paystack could not verify the bank account.");

            var data = root.GetProperty("data");
            var verified = data.TryGetProperty("verified", out var verifiedElement) && verifiedElement.GetBoolean();
            var acceptsCredits = data.TryGetProperty("accountAcceptsCredits", out var credits) && credits.GetBoolean();
            var holderMatch = data.TryGetProperty("accountHolderMatch", out var holder) && holder.GetBoolean();
            var open = data.TryGetProperty("accountOpen", out var openElement) && openElement.GetBoolean();

            if (!verified || !acceptsCredits || !holderMatch || !open)
                return new(false, ReadString(data, "verificationMessage") ?? "The bank account could not be verified for payouts.");

            return new(true, ReadString(data, "verificationMessage") ?? "Bank account verified successfully.", bankAccount.Id.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack bank verification failed for bank account {BankAccountId}", bankAccount.Id);
            return new(false, "Unable to reach the bank verification provider.");
        }
    }

    private void AddAuth(HttpRequestMessage request) =>
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SecretKey);

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;
}
