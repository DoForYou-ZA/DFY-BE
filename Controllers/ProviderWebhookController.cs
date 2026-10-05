using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Services;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/payment")]
public sealed class ProviderWebhookController(
    AppDbContext context,
    IPaymentProvider paymentProvider,
    IPayoutProvider payoutProvider,
    ILogger<ProviderWebhookController> logger) : ControllerBase
{
    [HttpPost("webhook")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> PaymentWebhook(CancellationToken cancellationToken)
    {
        var webhook = await paymentProvider.ParseWebhookAsync(Request, cancellationToken);
        if (!webhook.Valid)
            return BadRequest();

        if (webhook.EventType == "charge.success")
        {
            if (string.IsNullOrWhiteSpace(webhook.Reference) ||
                !webhook.Amount.HasValue)
                return BadRequest();

            var task = await context.Tasks
                .FirstOrDefaultAsync(t => t.PaymentReference == webhook.Reference, cancellationToken);

            if (task == null)
            {
                logger.LogWarning("Payment webhook received for unknown reference {Reference}", webhook.Reference);
                return NotFound();
            }

            if (webhook.Amount.Value != task.Budget)
                return BadRequest();

            var verified = await paymentProvider.VerifyPaymentAsync(
                webhook.Reference,
                cancellationToken);

            if (!verified.Success ||
                !verified.Found ||
                !verified.Paid ||
                !verified.Amount.HasValue ||
                verified.Amount.Value != task.Budget)
            {
                logger.LogWarning("Payment {Reference} failed independent provider verification.", webhook.Reference);
                return BadRequest();
            }

            var payment = await context.Payments
                .FirstOrDefaultAsync(p => p.TaskId == task.Id, cancellationToken);

            if (payment != null)
            {
                payment.TransactionId = webhook.TransactionId ?? verified.TransactionId;
                payment.PaymentMethod = paymentProvider.Name;
                payment.Status = "Completed";
                payment.CompletedAt ??= DateTime.UtcNow;
            }

            var transactionId = webhook.TransactionId ?? verified.TransactionId;
            var alreadyLogged = !string.IsNullOrWhiteSpace(transactionId) &&
                await context.AuditLogs.AnyAsync(a =>
                    a.EntityType == "Task" &&
                    a.EntityId == task.Id &&
                    a.Action == "PaymentVerified" &&
                    a.NewValues != null &&
                    a.NewValues.Contains($"transactionId={transactionId}"),
                    cancellationToken);

            if (!alreadyLogged)
            {
                context.AuditLogs.Add(new Models.AuditLog
                {
                    UserId = null,
                    Action = "PaymentVerified",
                    EntityType = "Task",
                    EntityId = task.Id,
                    NewValues = $"provider={paymentProvider.Name}; transactionId={transactionId}; transactionReference={webhook.Reference}; amount={task.Budget:F2}",
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                    UserAgent = Request.Headers.UserAgent.ToString()
                });
            }

            if (task.PaymentStatus != "EscrowHeld" || task.TaskStatus == "PendingPayment")
            {
                task.PaymentStatus = "EscrowHeld";
                task.TaskStatus = "Posted";
                task.EscrowStatus = "held";
                task.UpdatedAt = DateTime.UtcNow;
            }

            await context.SaveChangesAsync(cancellationToken);
            return Ok();
        }

        if (webhook.EventType.StartsWith("refund.", StringComparison.OrdinalIgnoreCase))
        {
            await HandleRefundWebhookAsync(webhook, cancellationToken);
            return Ok();
        }

        return Ok();
    }

    [HttpPost("payout-webhook")]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> PayoutWebhook(CancellationToken cancellationToken)
    {
        var webhook = await payoutProvider.ParseWebhookAsync(Request, cancellationToken);
        if (!webhook.Valid)
            return BadRequest();

        if (webhook.Status == ProviderPayoutStatus.Unknown)
            return Ok();

        var payout = await context.Payouts
            .Include(p => p.Task)
            .FirstOrDefaultAsync(
                p => (webhook.PayoutId != null && p.ProviderReference == webhook.PayoutId) ||
                     (webhook.MerchantReference != null && p.MerchantReference == webhook.MerchantReference),
                cancellationToken);

        if (payout == null)
        {
            logger.LogWarning("Payout webhook received for unknown payout {Reference}", webhook.PayoutId ?? webhook.MerchantReference);
            return Ok();
        }

        payout.Provider = payoutProvider.Name;
        payout.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(webhook.PayoutId))
            payout.ProviderReference = webhook.PayoutId;

        if (webhook.Status == ProviderPayoutStatus.Completed)
        {
            var completedAt = payout.CompletedAt ?? DateTime.UtcNow;
            payout.Status = "Completed";
            payout.CompletedAt = completedAt;
            payout.FailureReason = null;
            payout.ProcessingError = null;
            payout.NextAttemptAt = null;
            payout.Task.PayoutStatus = "Completed";
            payout.Task.PayoutCompletedAt ??= completedAt;
            payout.Task.PaidToRunnerAt ??= completedAt;
            payout.Task.PaymentStatus = "PayoutCompleted";
            payout.Task.TaskStatus = "RunnerPaid";
            payout.Task.UpdatedAt = completedAt;
        }
        else if (!IsTerminalCompleted(payout))
        {
            payout.Status = webhook.Status switch
            {
                ProviderPayoutStatus.Returned => "Returned",
                ProviderPayoutStatus.Cancelled => "Cancelled",
                ProviderPayoutStatus.Failed => "Failed",
                _ => "Processing"
            };

            payout.FailureReason = webhook.Error;
            payout.NextAttemptAt = null;
            payout.Task.PayoutStatus = payout.Status;
            payout.Task.TaskStatus = payout.Status switch
            {
                "Returned" => "PayoutReturned",
                "Cancelled" => "PayoutCancelled",
                "Failed" => "PayoutFailed",
                _ => "PayoutPending"
            };
        }

        await context.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    [HttpGet("return")]
    [AllowAnonymous]
    public IActionResult PaymentReturn([FromQuery] string? taskId = null)
    {
        var frontendUrl = Environment.GetEnvironmentVariable("FRONTEND_URL")
            ?? "https://do-for-you.vercel.app";
        var url = string.IsNullOrWhiteSpace(taskId)
            ? $"{frontendUrl}/payment/success"
            : $"{frontendUrl}/payment/success?taskId={Uri.EscapeDataString(taskId)}";
        return Redirect(url);
    }

    [HttpGet("cancel")]
    [AllowAnonymous]
    public IActionResult PaymentCancel([FromQuery] string? taskId = null)
    {
        var frontendUrl = Environment.GetEnvironmentVariable("FRONTEND_URL")
            ?? "https://do-for-you.vercel.app";
        var url = string.IsNullOrWhiteSpace(taskId)
            ? $"{frontendUrl}/payment/cancelled"
            : $"{frontendUrl}/payment/cancelled?taskId={Uri.EscapeDataString(taskId)}";
        return Redirect(url);
    }

    private async Task HandleRefundWebhookAsync(
        PaymentWebhookEvent webhook,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhook.RefundId))
            return;

        var refund = await context.Refunds
            .FirstOrDefaultAsync(r => r.RefundId == webhook.RefundId, cancellationToken);

        if (refund == null)
            return;

        var task = await context.Tasks
            .FirstOrDefaultAsync(t => t.Id == refund.TaskId, cancellationToken);

        if (task == null)
            return;

        refund.Status = webhook.EventType switch
        {
            "refund.processed" => "Complete",
            "refund.failed" => "Failed",
            "refund.needs-attention" => "PendingInvestigation",
            "refund.processing" => "Submitted",
            "refund.pending" => "Submitted",
            _ => refund.Status
        };
        refund.LastReconciledAt = DateTime.UtcNow;

        if (refund.Status == "Complete")
        {
            refund.CompletedAt = DateTime.UtcNow;
            task.PaymentStatus = "Refunded";
            task.TaskStatus = "Cancelled";
            task.EscrowStatus = "refunded";
            task.PayoutStatus = "NotStarted";
        }
        else if (refund.Status == "Failed")
        {
            refund.FailureReason = "Payment provider reported that the refund failed.";
            task.PaymentStatus = "DisputePending";
            task.EscrowStatus = "disputed";
        }

        task.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static bool IsTerminalCompleted(Models.Payout payout) =>
        payout.Status == "Completed" ||
        payout.Task.PayoutStatus == "Completed" ||
        payout.Task.TaskStatus == "RunnerPaid";
}
