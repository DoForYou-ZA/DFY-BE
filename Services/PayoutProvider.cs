using System.Globalization;
using System.Text.Json;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Http;

namespace DoForYou.API.Services;

public enum ProviderPayoutStatus
{
    Unknown,
    Pending,
    Processing,
    Completed,
    Failed,
    Returned,
    Cancelled,
    AwaitingFunds,
    AwaitingBankDetails
}

public record PayoutSubmissionResult(bool Success, string? PayoutId, bool Retryable, string? Error);
public record PayoutReconciliationResult(
    bool Success,
    bool Found,
    string? PayoutId,
    ProviderPayoutStatus Status,
    string? ErrorMessage,
    bool RetrySafe,
    string? Error);
public record PayoutWebhookEvent(
    bool Valid,
    string EventType,
    string? MerchantReference,
    string? PayoutId,
    ProviderPayoutStatus Status,
    string? Error);

public interface IPayoutProvider
{
    string Name { get; }
    Task<PayoutSubmissionResult> RequestPayoutAsync(Payout payout, BankAccount bankAccount, CancellationToken cancellationToken = default);
    Task<PayoutReconciliationResult> GetPayoutByReferenceAsync(Payout payout, CancellationToken cancellationToken = default);
    Task<PayoutWebhookEvent> ParseWebhookAsync(HttpRequest request, CancellationToken cancellationToken = default);
}

public sealed class PaystackPayoutProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<PaystackPayoutProvider> logger) : IPayoutProvider
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

    public async Task<PayoutSubmissionResult> RequestPayoutAsync(
        Payout payout,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, null, false, "Paystack is not configured.");

        if (string.IsNullOrWhiteSpace(bankAccount.BankGroupId))
            return new(false, null, false, "Runner bank code is missing.");

        var recipient = await CreateRecipientAsync(bankAccount, cancellationToken);
        if (!recipient.Success || string.IsNullOrWhiteSpace(recipient.RecipientCode))
            return new(false, null, recipient.Retryable, recipient.Error ?? "Unable to create Paystack transfer recipient.");

        var reference = NormalizeTransferReference(payout.MerchantReference);

        try
        {
            var payload = new
            {
                source = "balance",
                amount = ToSubunit(payout.Amount),
                recipient = recipient.RecipientCode,
                reference,
                reason = $"DFY task {payout.TaskId}",
                currency = "ZAR"
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/transfer");
            AddAuth(request);
            request.Content = JsonContent.Create(payload);

            using var response = await Client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode)
                return new(false, null, IsRetryableHttp(response.StatusCode), ReadString(root, "message") ?? $"Paystack transfer failed ({(int)response.StatusCode}).");

            if (!root.TryGetProperty("status", out var apiStatus) || !apiStatus.GetBoolean())
                return new(false, null, false, ReadString(root, "message") ?? "Paystack transfer was rejected.");

            if (!root.TryGetProperty("data", out var data))
                return new(false, null, true, "Paystack returned no transfer data.");

            var providerReference = ReadString(data, "reference") ?? reference;
            var status = ReadString(data, "status");

            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                return new(true, providerReference, false, null);

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "reversed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "abandoned", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase))
                return new(false, providerReference, false, $"Paystack transfer status: {status}.");

            return new(true, providerReference, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Paystack payout request failed for {Reference}", payout.MerchantReference);
            return new(false, null, true, "Paystack payout request was ambiguous and requires reconciliation.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack payout request failed for {Reference}", payout.MerchantReference);
            return new(false, null, true, "Paystack payout request requires reconciliation.");
        }
    }

    public async Task<PayoutReconciliationResult> GetPayoutByReferenceAsync(
        Payout payout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, false, null, ProviderPayoutStatus.Unknown, null, false, "Paystack is not configured.");

        var reference = NormalizeTransferReference(payout.MerchantReference);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl}/transfer/verify/{Uri.EscapeDataString(reference)}");
            AddAuth(request);

            using var response = await Client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new(true, false, null, ProviderPayoutStatus.Unknown, null, true, null);

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode ||
                !root.TryGetProperty("status", out var apiStatus) ||
                !apiStatus.GetBoolean() ||
                !root.TryGetProperty("data", out var data))
            {
                return new(false, false, null, ProviderPayoutStatus.Unknown, null, false, ReadString(root, "message") ?? "Unable to reconcile Paystack payout.");
            }

            var providerReference = ReadString(data, "reference") ?? reference;
            var status = MapStatus(ReadString(data, "status"));
            var message = ReadString(data, "failures");

            return new(true, true, providerReference, status, message, false, null);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Paystack payout reconciliation failed for {Reference}", payout.MerchantReference);
            return new(false, false, null, ProviderPayoutStatus.Unknown, null, false, "Paystack payout reconciliation is unresolved.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack payout reconciliation failed for {Reference}", payout.MerchantReference);
            return new(false, false, null, ProviderPayoutStatus.Unknown, null, false, "Paystack payout reconciliation is unresolved.");
        }
    }

    public async Task<PayoutWebhookEvent> ParseWebhookAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, string.Empty, null, null, ProviderPayoutStatus.Unknown, "Paystack is not configured.");

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken);
        request.Body.Position = 0;

        var supplied = request.Headers["x-paystack-signature"].ToString();
        var expected = Convert.ToHexString(
            System.Security.Cryptography.HMACSHA512.HashData(
                System.Text.Encoding.UTF8.GetBytes(SecretKey),
                System.Text.Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(supplied) ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected),
                System.Text.Encoding.UTF8.GetBytes(supplied.Trim().ToLowerInvariant())))
            return new(false, string.Empty, null, null, ProviderPayoutStatus.Unknown, "Invalid Paystack signature.");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var eventType = ReadString(root, "event") ?? string.Empty;
        var data = root.TryGetProperty("data", out var d) ? d : default;

        var status = eventType switch
        {
            "transfer.success" => ProviderPayoutStatus.Completed,
            "transfer.failed" => ProviderPayoutStatus.Failed,
            "transfer.reversed" => ProviderPayoutStatus.Returned,
            _ => ProviderPayoutStatus.Unknown
        };

        var reference = ReadString(data, "reference");
        var payoutId = ReadString(data, "transfer_code") ?? (data.TryGetProperty("id", out var id) ? id.ToString() : null);

        return new(true, eventType, reference, payoutId, status, null);
    }

    private async Task<(bool Success, string? RecipientCode, bool Retryable, string? Error)> CreateRecipientAsync(
        BankAccount bankAccount,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            type = "basa",
            name = bankAccount.AccountHolderName,
            account_number = bankAccount.AccountNumber,
            bank_code = bankAccount.BankGroupId,
            currency = "ZAR"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/transferrecipient");
        AddAuth(request);
        request.Content = JsonContent.Create(payload);

        try
        {
            using var response = await Client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode)
                return (false, null, IsRetryableHttp(response.StatusCode), ReadString(root, "message"));

            if (!root.TryGetProperty("status", out var status) || !status.GetBoolean())
                return (false, null, false, ReadString(root, "message") ?? "Paystack rejected the bank account.");

            var data = root.GetProperty("data");
            var code = ReadString(data, "recipient_code");
            return (true, code, false, null);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Paystack recipient creation failed for bank account {BankAccountId}", bankAccount.Id);
            return (false, null, true, "Unable to create Paystack transfer recipient.");
        }
    }

    private void AddAuth(HttpRequestMessage request) =>
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SecretKey);

    private static long ToSubunit(decimal amount) =>
        checked((long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero));

    private static bool IsRetryableHttp(System.Net.HttpStatusCode statusCode) =>
        (int)statusCode >= 500 || statusCode == System.Net.HttpStatusCode.RequestTimeout || statusCode == System.Net.HttpStatusCode.TooManyRequests;

    private static ProviderPayoutStatus MapStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "pending" or "otp" or "received" or "blocked" => ProviderPayoutStatus.Processing,
            "success" => ProviderPayoutStatus.Completed,
            "failed" or "abandoned" or "rejected" => ProviderPayoutStatus.Failed,
            "reversed" => ProviderPayoutStatus.Returned,
            _ => ProviderPayoutStatus.Unknown
        };

    private static string NormalizeTransferReference(string reference)
    {
        var normalized = new string(reference
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray());

        if (normalized.Length < 16)
            normalized = normalized.PadRight(16, '0');

        return normalized.Length > 50 ? normalized[..50] : normalized;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;
}
