using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Services;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/payment")]
public class PaymentController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IPaymentProvider _paymentProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentController> _logger;
    private readonly INotificationService _notificationService;

    public PaymentController(
        AppDbContext context,
        IPaymentProvider paymentProvider,
        IConfiguration configuration,
        ILogger<PaymentController> logger,
        INotificationService notificationService)
    {
        _context = context;
        _paymentProvider = paymentProvider;
        _configuration = configuration;
        _logger = logger;
        _notificationService = notificationService;
    }

    [HttpPost("initiate")]
    [Authorize]
    public IActionResult InitiatePayment() =>
        StatusCode(StatusCodes.Status410Gone, new ApiResponse<object>
        {
            Success = false,
            Message = "Use the task payment workflow."
        });

    [HttpGet("wallet")]
    public ActionResult<ApiResponse<object>> GetWallet() =>
        StatusCode(StatusCodes.Status410Gone, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallets have been retired. Runner earnings are paid directly to the verified bank account."
        });

    [HttpPost("withdraw")]
    public ActionResult<ApiResponse<object>> WithdrawFunds() =>
        StatusCode(StatusCodes.Status410Gone, new ApiResponse<object>
        {
            Success = false,
            Message = "Runner wallet withdrawals have been retired. Completed task payouts are sent directly to the verified runner bank account."
        });

    [HttpPost("webhook")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [Consumes("application/json")]
    public async Task<IActionResult> Webhook()
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync();
        Request.Body.Position = 0;

        var signature = Request.Headers["x-paystack-signature"].FirstOrDefault();
        if (!_paymentProvider.VerifyWebhookSignature(rawBody, signature))
        {
            _logger.LogWarning("Rejected Paystack webhook because signature validation failed.");
            return BadRequest();
        }

        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            var eventName = root.TryGetProperty("event", out var eventElement)
                ? eventElement.GetString()
                : null;

            if (string.Equals(eventName, "charge.success", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleChargeSuccess(root);
            }

            if (eventName is "refund.processed" or "refund.failed" or "refund.pending" or "refund.processing")
            {
                return await HandleRefundEvent(root, eventName);
            }

            if (eventName is "transfer.success" or "transfer.failed" or "transfer.reversed")
            {
                return await HandleTransferEvent(root, eventName);
            }

            // Paystack may add new events over time. Unknown events are acknowledged
            // after signature verification so they do not trigger retries.
            return Ok();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid Paystack webhook JSON.");
            return BadRequest();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paystack webhook processing failed.");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<IActionResult> HandleChargeSuccess(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            return BadRequest();

        var reference = GetString(data, "reference");
        var amountMinor = GetDecimal(data, "amount");
        var transactionId = GetString(data, "id");

        if (string.IsNullOrWhiteSpace(reference) || !amountMinor.HasValue)
            return BadRequest();

        var paymentAttempt = await _context.PaymentAttempts
            .FirstOrDefaultAsync(p => p.Provider == "Paystack" && p.Reference == reference);

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.PaymentReference == reference);
        if (task == null && paymentAttempt != null)
            task = await _context.Tasks.FirstOrDefaultAsync(t => t.Id == paymentAttempt.TaskId);

        if (task == null)
        {
            _logger.LogWarning("Paystack charge.success received for unknown reference {Reference}.", reference);
            return NotFound();
        }

        var expectedAmount = task.Budget;
        var webhookAmount = amountMinor.Value / 100m;
        if (webhookAmount != expectedAmount)
        {
            _logger.LogWarning(
                "Paystack amount mismatch for {Reference}. Expected {Expected}, received {Received}.",
                reference, expectedAmount, webhookAmount);
            return BadRequest();
        }

        // Webhooks are a notification mechanism. Independently verify the transaction
        // against Paystack before releasing the task into the DFY workflow.
        var verified = await _paymentProvider.VerifyPaymentAsync(
            reference,
            HttpContext.RequestAborted);

        if (!verified.Success ||
            !verified.Found ||
            !string.Equals(verified.Status, "success", StringComparison.OrdinalIgnoreCase) ||
            !verified.Amount.HasValue ||
            verified.Amount.Value != expectedAmount)
        {
            _logger.LogWarning("Paystack payment {Reference} failed independent verification.", reference);
            return BadRequest();
        }

        var tx = transactionId ?? verified.TransactionId;

        if (paymentAttempt != null)
        {
            var priorCompleted = await _context.PaymentAttempts
                .AnyAsync(p =>
                    p.TaskId == task.Id &&
                    p.Status == "Completed" &&
                    p.TransactionId != null &&
                    p.TransactionId != tx);

            if (priorCompleted)
            {
                paymentAttempt.Status = "Duplicate";
                paymentAttempt.TransactionId = tx;
                paymentAttempt.FailureReason = "Another successful Paystack payment already activated this task.";
                paymentAttempt.CompletedAt = DateTime.UtcNow;
                paymentAttempt.FailureReason = "Duplicate successful payment requires admin review/refund.";
                _context.AuditLogs.Add(new Models.AuditLog
                {
                    UserId = null,
                    Action = "PaystackDuplicatePaymentDetected",
                    EntityType = "Task",
                    EntityId = task.Id,
                    NewValues = $"transactionId={tx}; transactionReference={reference}; amount={expectedAmount:F2}",
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                    UserAgent = Request.Headers.UserAgent.ToString()
                });
                await _context.SaveChangesAsync();
                return Ok();
            }

            paymentAttempt.Status = "Completed";
            paymentAttempt.TransactionId = tx;
            paymentAttempt.CompletedAt ??= DateTime.UtcNow;
            paymentAttempt.FailureReason = null;
        }

        var payment = await _context.Payments.FirstOrDefaultAsync(p => p.TaskId == task.Id);
        if (payment == null)
        {
            _context.Payments.Add(new Models.Payment
            {
                PaymentId = $"DFY-PAY-{task.TaskId}",
                TaskId = task.Id,
                Amount = task.Budget,
                PaymentMethod = "Online",
                Provider = "Paystack",
                Status = "Completed",
                TransactionId = tx,
                CreatedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            });
        }
        else
        {
            payment.Provider = "Paystack";
            payment.TransactionId = tx;
            payment.Status = "Completed";
            payment.CompletedAt ??= DateTime.UtcNow;
        }
        if (!string.IsNullOrWhiteSpace(tx))
        {
            var alreadyAudited = await _context.AuditLogs.AnyAsync(a =>
                a.EntityType == "Task" &&
                a.EntityId == task.Id &&
                a.Action == "PaystackPaymentVerified" &&
                a.NewValues != null &&
                a.NewValues.Contains($"transactionId={tx}"));

            if (!alreadyAudited)
            {
                _context.AuditLogs.Add(new Models.AuditLog
                {
                    UserId = null,
                    Action = "PaystackPaymentVerified",
                    EntityType = "Task",
                    EntityId = task.Id,
                    NewValues = $"transactionId={tx}; transactionReference={reference}; amount={expectedAmount:F2}",
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                    UserAgent = Request.Headers.UserAgent.ToString()
                });
            }
        }

        // Idempotent task activation.
        if (task.PaymentStatus != "EscrowHeld" || task.TaskStatus == "PendingPayment")
        {
            task.PaymentStatus = "EscrowHeld";
            task.TaskStatus = "Posted";
            task.EscrowStatus = "held";
            task.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return Ok();
    }

    private async Task<IActionResult> HandleRefundEvent(JsonElement root, string eventName)
    {
        if (!root.TryGetProperty("data", out var data))
            return BadRequest();

        var refundId = GetString(data, "id");
        var transaction = data.TryGetProperty("transaction", out var transactionElement)
            ? transactionElement.ToString()
            : null;
        var amountMinor = GetDecimal(data, "amount");
        var providerStatus = GetString(data, "status");

        if (string.IsNullOrWhiteSpace(refundId))
            return BadRequest();

        var refund = await _context.Refunds.FirstOrDefaultAsync(r =>
            r.RefundId == refundId ||
            (!string.IsNullOrWhiteSpace(transaction) && r.TransactionId == transaction));

        if (refund == null)
        {
            _logger.LogWarning("Paystack refund event {RefundId} has no matching DFY refund.", refundId);
            return NotFound();
        }

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.Id == refund.TaskId);
        if (task == null)
            return NotFound();

        refund.LastReconciledAt = DateTime.UtcNow;

        if (eventName == "refund.processed" ||
            string.Equals(providerStatus, "processed", StringComparison.OrdinalIgnoreCase))
        {
            refund.Status = "Complete";
            refund.CompletedAt = DateTime.UtcNow;

            task.PaymentStatus = "Refunded";
            task.TaskStatus = "Cancelled";
            task.EscrowStatus = "refunded";
            task.PayoutStatus = "NotStarted";
            task.PayoutReference = null;
            task.PayoutInitiatedAt = null;
            task.PayoutCompletedAt = null;
            task.PaidToRunnerAt = null;
            task.UpdatedAt = DateTime.UtcNow;

            var disputes = await _context.Disputes
                .Where(d => d.TaskId == task.Id && d.Status == "RefundPending")
                .ToListAsync();

            foreach (var dispute in disputes)
            {
                dispute.Status = "Resolved";
                dispute.ResolvedAt = DateTime.UtcNow;
            }

            _context.AuditLogs.Add(new Models.AuditLog
            {
                UserId = null,
                Action = "PaystackRefundCompleted",
                EntityType = "Task",
                EntityId = task.Id,
                NewValues = $"refundId={refundId}; transactionId={transaction}; amount={(amountMinor ?? 0m) / 100m:F2}",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                UserAgent = Request.Headers.UserAgent.ToString()
            });
        }
        else if (eventName == "refund.failed" ||
                 string.Equals(providerStatus, "failed", StringComparison.OrdinalIgnoreCase))
        {
            refund.Status = "Failed";
            refund.FailureReason = GetString(data, "customer_note") ?? "Paystack refund failed.";

            task.PaymentStatus = "DisputePending";
            task.TaskStatus = "Completed";
            task.EscrowStatus = "disputed";
            task.UpdatedAt = DateTime.UtcNow;

            var disputes = await _context.Disputes
                .Where(d => d.TaskId == task.Id && d.Status == "RefundPending")
                .ToListAsync();

            foreach (var dispute in disputes)
                dispute.Status = "Open";
        }
        else
        {
            refund.Status = eventName.Replace("refund.", "", StringComparison.OrdinalIgnoreCase);
        }

        await _context.SaveChangesAsync();
        return Ok();
    }



    private async Task<IActionResult> HandleTransferEvent(JsonElement root, string eventName)
    {
        if (!root.TryGetProperty("data", out var data))
            return BadRequest();

        var reference = GetString(data, "reference");
        var amountMinor = GetDecimal(data, "amount");

        if (string.IsNullOrWhiteSpace(reference) || !amountMinor.HasValue)
            return BadRequest();

        var normalizedReference = NormalizeProviderReference(reference);

        var payouts = await _context.Payouts
            .Include(p => p.Task)
            .Where(p => p.Provider == "Paystack")
            .ToListAsync();

        var payout = payouts.FirstOrDefault(p =>
            NormalizeProviderReference(p.MerchantReference) == normalizedReference);

        if (payout == null)
        {
            _logger.LogWarning("Paystack transfer event {Reference} has no matching DFY payout.", reference);
            return NotFound();
        }

        if (amountMinor.Value / 100m != payout.Amount)
        {
            _logger.LogWarning(
                "Paystack payout amount mismatch for {Reference}. Expected {Expected}, received {Received}.",
                reference, payout.Amount, amountMinor.Value / 100m);
            return BadRequest();
        }

        payout.ProviderReference = GetString(data, "transfer_code") ?? payout.ProviderReference;
        payout.UpdatedAt = DateTime.UtcNow;

        if (eventName == "transfer.success")
        {
            payout.Status = "Completed";
            payout.CompletedAt ??= DateTime.UtcNow;
            payout.FailureReason = null;
            payout.ProcessingError = null;
            payout.NextAttemptAt = null;

            if (payout.Task.TaskStatus != "RunnerPaid")
            {
                payout.Task.PayoutStatus = "Completed";
                payout.Task.TaskStatus = "RunnerPaid";
                payout.Task.PayoutCompletedAt = DateTime.UtcNow;
                payout.Task.PaidToRunnerAt = DateTime.UtcNow;
                payout.Task.UpdatedAt = DateTime.UtcNow;

                await _notificationService.NotifyPaymentReleasedAsync(
                    payout.RunnerId,
                    payout.Task.TaskDescription,
                    payout.Amount);
            }
        }
        else if (eventName == "transfer.reversed")
        {
            payout.Status = "Returned";
            payout.FailureReason = "Paystack reversed the runner payout.";
            payout.ProcessingError = null;
            payout.NextAttemptAt = null;
            payout.Task.PayoutStatus = "Returned";
            payout.Task.TaskStatus = "PayoutFailed";
            payout.Task.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            payout.Status = "Failed";
            payout.FailureReason = "Paystack transfer failed.";
            payout.ProcessingError = null;
            payout.NextAttemptAt = null;
            payout.Task.PayoutStatus = "Failed";
            payout.Task.TaskStatus = "PayoutFailed";
            payout.Task.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("return")]
    [AllowAnonymous]
    public IActionResult PaymentReturn([FromQuery] string? taskId = null)
    {
        var frontendUrl = (
            Environment.GetEnvironmentVariable("FRONTEND_URL")
            ?? _configuration["FrontendUrl"]
            ?? "https://do-for-you.vercel.app").TrimEnd('/');

        var redirect = string.IsNullOrEmpty(taskId)
            ? $"{frontendUrl}/payments/success"
            : $"{frontendUrl}/payment/success?taskId={Uri.EscapeDataString(taskId)}";

        return Redirect(redirect);
    }

    [HttpGet("cancel")]
    [AllowAnonymous]
    public IActionResult PaymentCancel([FromQuery] string? taskId = null)
    {
        var frontendUrl = (
            Environment.GetEnvironmentVariable("FRONTEND_URL")
            ?? _configuration["FrontendUrl"]
            ?? "https://do-for-you.vercel.app").TrimEnd('/');

        var redirect = string.IsNullOrEmpty(taskId)
            ? $"{frontendUrl}/payments/cancelled"
            : $"{frontendUrl}/payment/cancelled?taskId={Uri.EscapeDataString(taskId)}";

        return Redirect(redirect);
    }

    private static string NormalizeProviderReference(string reference) =>
        new string(reference.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray());

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static decimal? GetDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        return decimal.TryParse(
            value.ToString(),
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }
}
