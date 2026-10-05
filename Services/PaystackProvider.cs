using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

public sealed class PaystackProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<PaystackProvider> logger)
    : IPaymentProvider, IPayoutProvider, IBankProvider
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private HttpClient Client => httpClientFactory.CreateClient("Paystack");

    private string SecretKey =>
        Environment.GetEnvironmentVariable("PAYSTACK_SECRET_KEY")
        ?? configuration["Paystack:SecretKey"]
        ?? string.Empty;

    private string BaseUrl =>
        (Environment.GetEnvironmentVariable("PAYSTACK_BASE_URL")
        ?? configuration["Paystack:BaseUrl"]
        ?? "https://api.paystack.co").TrimEnd('/');

    private string BackendUrl =>
        (Environment.GetEnvironmentVariable("BACKEND_URL")
        ?? configuration["BackendUrl"]
        ?? "https://api.doforyou.co.za").TrimEnd('/');

    private string FrontendUrl =>
        (Environment.GetEnvironmentVariable("FRONTEND_URL")
        ?? configuration["FrontendUrl"]
        ?? "https://do-for-you.vercel.app").TrimEnd('/');

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{BaseUrl}/{path.TrimStart('/')}");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SecretKey);
        return request;
    }

    public async Task<PaymentCreationResult> CreatePaymentAsync(
        Models.Task task,
        User customer,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, null, null, "Paystack secret key is not configured.");

        var reference = string.IsNullOrWhiteSpace(task.PaymentReference)
            ? $"dfy_pay_{task.TaskId}".ToLowerInvariant()
            : task.PaymentReference;

        var payload = new
        {
            email = customer.Email,
            amount = checked((long)Math.Round(task.Budget * 100m)),
            currency = "ZAR",
            reference,
            callback_url = $"{BackendUrl}/api/v1/payment/return?taskId={Uri.EscapeDataString(task.TaskId)}",
            metadata = new
            {
                taskId = task.TaskId,
                dfyTaskId = task.Id
            }
        };

        try
        {
            using var request = Request(HttpMethod.Post, "transaction/initialize");
            request.Content = JsonContent.Create(payload, options: _json);

            using var response = await Client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || !GetBool(root, "status"))
                return new(false, null, reference, GetString(root, "message") ?? "Paystack payment initialization failed.");

            var data = root.GetProperty("data");
            var url = GetString(data, "authorization_url");

            if (string.IsNullOrWhiteSpace(url))
                return new(false, null, reference, "Paystack did not return a checkout URL.");

            return new(true, url, reference, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack payment initialization failed for {Reference}", reference);
            return new(false, null, reference, "Unable to create the Paystack payment.");
        }
    }

    public async Task<PaymentVerificationResult> VerifyPaymentAsync(
        string transactionReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, false, null, null, null, transactionReference, "Paystack secret key is not configured.");

        try
        {
            using var request = Request(
                HttpMethod.Get,
                $"transaction/verify/{Uri.EscapeDataString(transactionReference)}");

            using var response = await Client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || !GetBool(root, "status"))
                return new(false, false, null, null, null, transactionReference,
                    GetString(root, "message") ?? "Paystack transaction verification failed.");

            var data = root.GetProperty("data");
            var amountMinor = GetDecimal(data, "amount");

            return new(
                true,
                true,
                GetString(data, "id"),
                GetString(data, "status"),
                amountMinor.HasValue ? amountMinor.Value / 100m : null,
                GetString(data, "reference") ?? transactionReference,
                null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Paystack payment verification failed for {Reference}", transactionReference);
            return new(false, false, null, null, null, transactionReference,
                "Unable to verify the Paystack transaction.");
        }
    }

    public async Task<RefundResult> CreateRefundAsync(
        string transactionId,
        decimal amount,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, null, transactionId, null, null, "Paystack secret key is not configured.");

        var payload = new
        {
            transaction = transactionId,
            amount = checked((long)Math.Round(amount * 100m)),
            currency = "ZAR",
            customer_note = reason,
            merchant_note = $"DFY refund: {reason}"
        };

        try
        {
            using var request = Request(HttpMethod.Post, "refund");
            request.Content = JsonContent.Create(payload, options: _json);

            using var response = await Client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || !GetBool(root, "status"))
                return new(false, null, transactionId, null, null,
                    GetString(root, "message") ?? "Paystack refund request failed.");

            var data = root.GetProperty("data");
            return new(
                true,
                GetString(data, "id"),
                GetString(data, "transaction") ?? transactionId,
                GetDecimal(data, "amount") is { } refundMinor ? refundMinor / 100m : amount,
                GetString(data, "status"),
                null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack refund failed for transaction {TransactionId}", transactionId);
            return new(false, null, transactionId, null, null, "Unable to submit the Paystack refund.");
        }
    }

    public bool VerifyWebhookSignature(string rawBody, string? signature)
    {
        if (string.IsNullOrWhiteSpace(SecretKey) || string.IsNullOrWhiteSpace(signature))
            return false;

        var expected = Convert.ToHexString(
            HMACSHA512.HashData(
                Encoding.UTF8.GetBytes(SecretKey),
                Encoding.UTF8.GetBytes(rawBody)))
            .ToLowerInvariant();

        var supplied = signature.Trim().ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(supplied));
    }

    public async Task<IReadOnlyList<SupportedBank>> GetSupportedBanksAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return Array.Empty<SupportedBank>();

        try
        {
            using var request = Request(
                HttpMethod.Get,
                "bank?currency=ZAR&perPage=100&enabled_for_verification=true");

            using var response = await Client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<SupportedBank>();

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return Array.Empty<SupportedBank>();

            return data.EnumerateArray()
                .Select(x => new SupportedBank(
                    GetString(x, "code") ?? string.Empty,
                    GetString(x, "name") ?? string.Empty,
                    GetString(x, "branch_code")))
                .Where(x => !string.IsNullOrWhiteSpace(x.Code) &&
                            !string.IsNullOrWhiteSpace(x.Name))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load Paystack South African bank list.");
            return Array.Empty<SupportedBank>();
        }
    }

    public async Task<BankVerificationResultV2> VerifyBankAccountAsync(
        User user,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user.IdNumber))
            return new(false, "Your South African ID number is required before the bank account can be verified.");

        if (string.IsNullOrWhiteSpace(bankAccount.BankCode))
            return new(false, "A Paystack-supported bank must be selected.");

        var payload = new
        {
            bank_code = bankAccount.BankCode,
            country_code = "ZA",
            account_number = bankAccount.AccountNumber,
            account_name = bankAccount.AccountHolderName,
            account_type = "personal",
            document_type = "identityNumber",
            document_number = user.IdNumber
        };

        try
        {
            using var request = Request(HttpMethod.Post, "bank/validate");
            request.Content = JsonContent.Create(payload, options: _json);

            using var response = await Client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || !GetBool(root, "status"))
                return new(false, GetString(root, "message") ?? "Paystack bank verification failed.");

            var data = root.GetProperty("data");
            var verified = GetBool(data, "verified");
            var credits = !data.TryGetProperty("accountAcceptsCredits", out _)
                || GetBool(data, "accountAcceptsCredits");

            if (!verified || !credits)
                return new(false,
                    GetString(data, "verificationMessage") ??
                    "The bank account could not be verified for payouts.");

            var recipient = await CreateRecipientAsync(
                bankAccount,
                cancellationToken);

            if (!recipient.Success)
                return new(false, recipient.Message);

            return new(
                true,
                "Bank account verified and payout recipient created.",
                recipient.ProviderReference);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack bank verification failed for bank account {BankAccountId}", bankAccount.Id);
            return new(false, "Bank verification is temporarily unavailable.");
        }
    }

    private async Task<(bool Success, string Message, string? ProviderReference)> CreateRecipientAsync(
        BankAccount bankAccount,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            type = "basa",
            name = bankAccount.AccountHolderName,
            account_number = bankAccount.AccountNumber,
            bank_code = bankAccount.BankCode,
            currency = "ZAR",
            description = $"DFY runner bank account {bankAccount.Id}"
        };

        using var request = Request(HttpMethod.Post, "transferrecipient");
        request.Content = JsonContent.Create(payload, options: _json);

        using var response = await Client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!response.IsSuccessStatusCode || !GetBool(root, "status"))
            return (false, GetString(root, "message") ?? "Paystack could not create the payout recipient.", null);

        var data = root.GetProperty("data");
        var recipientCode = GetString(data, "recipient_code");

        return string.IsNullOrWhiteSpace(recipientCode)
            ? (false, "Paystack did not return a payout recipient reference.", null)
            : (true, "Recipient created.", recipientCode);
    }

    public async Task<PayoutSubmissionResult> CreatePayoutAsync(
        Payout payout,
        BankAccount bankAccount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, null, false, "Paystack secret key is not configured.");

        if (string.IsNullOrWhiteSpace(bankAccount.ProviderReference))
            return new(false, null, false, "Runner bank account is missing its Paystack recipient reference.");

        var reference = NormalizeTransferReference(payout.MerchantReference);
        var payload = new
        {
            source = "balance",
            amount = checked((long)Math.Round(payout.Amount * 100m)),
            recipient = bankAccount.ProviderReference,
            reference,
            reason = $"DFY task payout {payout.TaskId}",
            currency = "ZAR"
        };

        try
        {
            using var request = Request(HttpMethod.Post, "transfer");
            request.Content = JsonContent.Create(payload, options: _json);

            using var response = await Client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || !GetBool(root, "status"))
            {
                var retryable = (int)response.StatusCode >= 500 ||
                                (int)response.StatusCode == 408 ||
                                (int)response.StatusCode == 429;
                return new(false, null, retryable,
                    GetString(root, "message") ?? $"Paystack transfer failed ({(int)response.StatusCode}).");
            }

            var data = root.GetProperty("data");
            var providerReference = GetString(data, "transfer_code")
                                    ?? GetString(data, "reference");

            return string.IsNullOrWhiteSpace(providerReference)
                ? new(false, null, true, "Paystack did not return a transfer reference.")
                : new(true, providerReference, false, null);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Paystack payout request failed for {Reference}", payout.MerchantReference);
            return new(false, null, true, "Unable to reach Paystack.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, null, true, "Paystack payout request timed out.");
        }
    }

    public async Task<PayoutStatusResult> GetPayoutStatusAsync(
        Payout payout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = Request(
                HttpMethod.Get,
                $"transfer/verify/{Uri.EscapeDataString(NormalizeTransferReference(payout.MerchantReference))}");

            using var response = await Client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new(true, false, null, null, null);

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || !GetBool(root, "status"))
                return new(false, false, null, null,
                    GetString(root, "message") ?? "Paystack payout verification failed.");

            var data = root.GetProperty("data");
            return new(
                true,
                true,
                GetString(data, "transfer_code") ?? GetString(data, "reference"),
                GetString(data, "status"),
                null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Paystack payout reconciliation failed for {Reference}", payout.MerchantReference);
            return new(false, false, null, null, "Unable to reconcile Paystack payout.");
        }
    }

    private static string NormalizeTransferReference(string reference)
    {
        var normalized = new string(reference
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray());

        if (normalized.Length < 16)
            normalized = $"dfy_payout_{normalized}";

        return normalized.Length > 50
            ? normalized[..50]
            : normalized;
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static decimal? GetDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out var number))
            return number;

        return decimal.TryParse(
            value.ToString(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }
}
