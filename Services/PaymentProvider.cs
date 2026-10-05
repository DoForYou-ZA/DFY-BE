using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoForYou.API.Models;
using Microsoft.AspNetCore.Http;

namespace DoForYou.API.Services;

public record PaymentCreationResult(bool Success, string? PaymentUrl, string? TransactionReference, string? Error);
public record PaymentVerificationResult(bool Success, bool Found, bool Paid, string? TransactionId, string? Status, decimal? Amount, string? Reference, string? Error);
public record RefundResult(bool Success, string? RefundId, string? TransactionId, decimal? Amount, string? Status, string? Error);
public record RefundLookup(string RefundId, string TransactionId, decimal Amount, string Status);
public record PaymentWebhookEvent(
    bool Valid,
    string EventType,
    string? Reference,
    string? TransactionId,
    decimal? Amount,
    bool PaymentSucceeded,
    string? RefundId,
    string? RefundStatus,
    string? Error);

public interface IPaymentProvider
{
    string Name { get; }
    Task<PaymentCreationResult> CreatePaymentAsync(Models.Task task, User customer, CancellationToken cancellationToken = default);
    Task<PaymentVerificationResult> VerifyPaymentAsync(string transactionReference, CancellationToken cancellationToken = default);
    Task<RefundResult> SubmitRefundAsync(string transactionId, decimal amount, string refundReason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RefundLookup>> GetRefundsByTransactionIdAsync(string transactionId, CancellationToken cancellationToken = default);
    Task<PaymentWebhookEvent> ParseWebhookAsync(HttpRequest request, CancellationToken cancellationToken = default);
}

public sealed class PaystackPaymentProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<PaystackPaymentProvider> logger) : IPaymentProvider
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

    public async Task<PaymentCreationResult> CreatePaymentAsync(
        Models.Task task,
        User customer,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, null, null, "Paystack is not configured.");

        var reference = string.IsNullOrWhiteSpace(task.PaymentReference)
            ? $"DFY-PAY-{task.TaskId}"
            : task.PaymentReference;

        var frontendUrl = Environment.GetEnvironmentVariable("FRONTEND_URL")
            ?? configuration["FrontendUrl"]
            ?? "https://do-for-you.vercel.app";

        var backendUrl = Environment.GetEnvironmentVariable("BACKEND_URL")
            ?? configuration["BackendUrl"]
            ?? "https://api.doforyou.co.za";

        var payload = new
        {
            amount = ToSubunit(task.Budget),
            email = customer.Email,
            currency = "ZAR",
            reference,
            callback_url = $"{backendUrl.TrimEnd('/')}/api/v1/payment/return?taskId={Uri.EscapeDataString(task.TaskId)}",
            metadata = JsonSerializer.Serialize(new { taskId = task.TaskId, platform = "DFY" })
        };

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{BaseUrl}/transaction/initialize");
            AddAuth(request);
            request.Content = JsonContent.Create(payload);

            using var response = await Client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var ok = root.TryGetProperty("status", out var status) && status.GetBoolean();

            if (!response.IsSuccessStatusCode || !ok)
                return new(false, null, reference, ReadString(root, "message") ?? $"Paystack payment initialization failed ({(int)response.StatusCode}).");

            var data = root.GetProperty("data");
            var url = ReadString(data, "authorization_url");
            var returnedReference = ReadString(data, "reference") ?? reference;

            if (string.IsNullOrWhiteSpace(url))
                return new(false, null, returnedReference, "Paystack did not return a checkout URL.");

            return new(true, url, returnedReference, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack payment initialization failed for task {TaskId}", task.TaskId);
            return new(false, null, reference, "Unable to create the Paystack payment.");
        }
    }

    public async Task<PaymentVerificationResult> VerifyPaymentAsync(
        string transactionReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, false, false, null, null, null, transactionReference, "Paystack is not configured.");

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl}/transaction/verify/{Uri.EscapeDataString(transactionReference)}");
            AddAuth(request);

            using var response = await Client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode ||
                !root.TryGetProperty("status", out var apiStatus) ||
                !apiStatus.GetBoolean() ||
                !root.TryGetProperty("data", out var data))
            {
                return new(false, false, false, null, null, null, transactionReference, ReadString(root, "message") ?? "Paystack transaction verification failed.");
            }

            var status = ReadString(data, "status");
            var reference = ReadString(data, "reference") ?? transactionReference;
            var transactionId = data.TryGetProperty("id", out var id) ? id.ToString() : null;
            var amount = ReadDecimalSubunit(data, "amount");
            var paid = string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);

            return new(true, true, paid, transactionId, status, amount, reference, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack transaction verification failed for {Reference}", transactionReference);
            return new(false, false, false, null, null, null, transactionReference, "Unable to verify the Paystack transaction.");
        }
    }

    public async Task<RefundResult> SubmitRefundAsync(
        string transactionId,
        decimal amount,
        string refundReason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, null, transactionId, null, null, "Paystack is not configured.");

        try
        {
            var payload = new
            {
                transaction = transactionId,
                amount = ToSubunit(amount),
                currency = "ZAR",
                customer_note = refundReason.Length > 500 ? refundReason[..500] : refundReason,
                merchant_note = $"DFY refund for transaction {transactionId}"
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/refund");
            AddAuth(request);
            request.Content = JsonContent.Create(payload);

            using var response = await Client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode ||
                !root.TryGetProperty("status", out var apiStatus) ||
                !apiStatus.GetBoolean())
                return new(false, null, transactionId, null, null, ReadString(root, "message") ?? "Paystack refund failed.");

            var data = root.GetProperty("data");
            var refundId = data.TryGetProperty("id", out var id) ? id.ToString() : null;
            var status = ReadString(data, "status") ?? "pending";
            var refundedAmount = ReadDecimalSubunit(data, "amount") ?? amount;

            if (string.IsNullOrWhiteSpace(refundId))
                return new(false, null, transactionId, null, status, "Paystack did not return a refund identifier.");

            return new(true, refundId, transactionId, refundedAmount, status, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack refund failed for transaction {TransactionId}", transactionId);
            return new(false, null, transactionId, null, null, "Unable to submit the Paystack refund.");
        }
    }

    public async Task<IReadOnlyList<RefundLookup>> GetRefundsByTransactionIdAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return Array.Empty<RefundLookup>();

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl}/refund?transaction={Uri.EscapeDataString(transactionId)}");
            AddAuth(request);

            using var response = await Client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<RefundLookup>();

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return Array.Empty<RefundLookup>();

            var results = new List<RefundLookup>();
            foreach (var item in data.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idElement) ? idElement.ToString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                results.Add(new RefundLookup(
                    id,
                    transactionId,
                    ReadDecimalSubunit(item, "amount") ?? 0m,
                    ReadString(item, "status") ?? "unknown"));
            }

            return results;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack refund lookup failed for transaction {TransactionId}", transactionId);
            return Array.Empty<RefundLookup>();
        }
    }

    public async Task<PaymentWebhookEvent> ParseWebhookAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
            return new(false, string.Empty, null, null, null, false, null, null, "Paystack is not configured.");

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken);
        request.Body.Position = 0;

        var supplied = request.Headers["x-paystack-signature"].ToString();
        if (string.IsNullOrWhiteSpace(supplied))
            return new(false, string.Empty, null, null, null, false, null, null, "Missing Paystack signature.");

        var expected = Convert.ToHexString(
            HMACSHA512.HashData(
                Encoding.UTF8.GetBytes(SecretKey),
                Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(supplied.Trim().ToLowerInvariant())))
            return new(false, string.Empty, null, null, null, false, null, null, "Invalid Paystack signature.");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var eventType = ReadString(root, "event") ?? string.Empty;
        var data = root.TryGetProperty("data", out var d) ? d : default;

        if (eventType == "charge.success")
        {
            return new(
                true,
                eventType,
                ReadString(data, "reference"),
                data.TryGetProperty("id", out var id) ? id.ToString() : null,
                ReadDecimalSubunit(data, "amount"),
                true,
                null,
                null,
                null);
        }

        if (eventType.StartsWith("refund.", StringComparison.OrdinalIgnoreCase))
        {
            var transaction = data.TryGetProperty("transaction", out var tx)
                ? tx.ToString()
                : null;

            return new(
                true,
                eventType,
                data.TryGetProperty("reference", out var reference) ? reference.ToString() : null,
                transaction,
                ReadDecimalSubunit(data, "amount"),
                false,
                data.TryGetProperty("id", out var refundId) ? refundId.ToString() : null,
                ReadString(data, "status"),
                null);
        }

        return new(true, eventType, null, null, null, false, null, null, null);
    }

    private void AddAuth(HttpRequestMessage request)
    {
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SecretKey);
    }

    private static long ToSubunit(decimal amount) =>
        checked((long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero));

    private static string? ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;
    }

    private static decimal? ReadDecimalSubunit(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number / 100m;

        return decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed / 100m
            : null;
    }
}
