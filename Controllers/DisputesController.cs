using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Services;
using System.Security.Claims;
using System.Text.Json;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/disputes")]
[Authorize]
public class DisputesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IEscrowService _escrowService;
    private readonly INotificationService _notificationService;
    private readonly IPaymentProvider _paymentProvider;

    public DisputesController(AppDbContext context, IEscrowService escrowService, INotificationService notificationService, IPaymentProvider paymentProvider)
    {
        _context = context;
        _escrowService = escrowService;
        _notificationService = notificationService;
        _paymentProvider = paymentProvider;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> RaiseDispute([FromBody] RaiseDisputeRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == request.TaskId);
        if (task == null)
            return Ok(new ApiResponse<object> { Success = false, Message = "Task not found" });

        if (task.CreatedByUserId != userId && task.AcceptedByUserId != userId)
            return Ok(new ApiResponse<object> { Success = false, Message = "Not authorized to dispute this task" });

        if (task.TaskStatus == "RunnerPaid" || task.EscrowStatus == "released" || task.EscrowStatus == "refunded")
            return Ok(new ApiResponse<object> { Success = false, Message = "This task is no longer eligible for a dispute" });

        var existingOpenDispute = await _context.Disputes
            .AnyAsync(d => d.TaskId == task.Id && d.Status == "Open");
        if (existingOpenDispute)
            return Ok(new ApiResponse<object> { Success = false, Message = "An open dispute already exists for this task" });

        var dispute = new Models.Dispute
        {
            TaskId = task.Id,
            ReportedByUserId = userId.Value,
            Issue = request.Issue,
            Category = request.Category,
            Status = "Open"
        };

        // A dispute must stop the normal 24-hour creator auto-release path. The task remains
        // completed/held so an admin can later release it into the Paystack payout flow.
        if (task.EscrowStatus == "held" || task.PaymentStatus == "EscrowHeld")
        {
            task.EscrowStatus = "disputed";
            task.PaymentStatus = "DisputePending";
            task.UpdatedAt = DateTime.UtcNow;
        }

        _context.Disputes.Add(dispute);
        await _context.SaveChangesAsync();

        var reporter = await _context.Users.FindAsync(userId.Value);
        var reporterName = reporter != null ? $"{reporter.FirstName} {reporter.LastName}" : "A user";
        await _notificationService.NotifyAdminsAsync(
            "dispute_raised",
            "New Dispute Raised",
            $"{reporterName} raised a dispute on task {task.TaskId}: {request.Issue.Substring(0, Math.Min(request.Issue.Length, 80))}",
            task.Id
        );

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { id = dispute.Id, status = dispute.Status },
            Message = "Dispute raised successfully. Escrow is frozen pending admin resolution."
        });
    }

    [HttpGet("my")]
    public async Task<ActionResult<ApiResponse<object>>> GetMyDisputes()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var disputes = await _context.Disputes
            .Include(d => d.Task)
            .Where(d => d.ReportedByUserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new
            {
                id = d.Id,
                taskId = d.Task.TaskId,
                taskDescription = d.Task.TaskDescription,
                issue = d.Issue,
                category = d.Category,
                status = d.Status,
                resolution = d.Resolution,
                createdAt = d.CreatedAt,
                resolvedAt = d.ResolvedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = disputes });
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<object>>> GetAllDisputes(
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _context.Disputes
            .Include(d => d.Task)
            .Include(d => d.ReportedByUser)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status)) query = query.Where(d => d.Status == status);

        var total = await query.CountAsync();
        var disputes = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                id = d.Id,
                taskId = d.Task.TaskId,
                taskDescription = d.Task.TaskDescription,
                reportedBy = $"{d.ReportedByUser.FirstName} {d.ReportedByUser.LastName}",
                issue = d.Issue,
                category = d.Category,
                status = d.Status,
                resolution = d.Resolution,
                createdAt = d.CreatedAt,
                resolvedAt = d.ResolvedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = new { disputes, total, page, pageSize } });
    }

    [HttpPatch("{id}/resolve")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<bool>>> ResolveDispute(int id, [FromBody] JsonElement request)
    {
        var action = request.TryGetProperty("action", out var actionElement) ? actionElement.GetString()?.Trim() : null;
        var resolution = request.TryGetProperty("resolution", out var resolutionElement) ? resolutionElement.GetString()?.Trim() : null;
        var reason = request.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString()?.Trim() : null;

        if (string.IsNullOrWhiteSpace(action) ||
            string.IsNullOrWhiteSpace(resolution) ||
            string.IsNullOrWhiteSpace(reason) ||
            reason.Length < 5 ||
            reason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Action, resolution and a reason between 5 and 500 characters are required." });

        var dispute = await _context.Disputes
            .Include(d => d.Task)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (dispute == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Dispute not found" });
        if (dispute.Status == "Resolved")
            return Ok(new ApiResponse<bool> { Success = false, Message = "Dispute is already resolved" });

        var task = dispute.Task;
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Dispute task not found" });

        if (action == "release_to_runner")
        {
            // Re-open the escrow hold for the release service, then create the normal
            // idempotent Paystack payout. The payout processor will only mark the runner paid
            // after Paystack confirms a successful transfer.
            task.EscrowStatus = "held";
            task.PaymentStatus = "EscrowHeld";
            task.EscrowHoldUntil = DateTime.UtcNow.AddSeconds(-1);
            task.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var released = await _escrowService.ReleaseEscrowAsync(task.Id, force: true);
            if (!released)
                return Ok(new ApiResponse<bool> { Success = false, Message = "Unable to release escrow into the Paystack payout flow" });
        }
        else if (action == "refund_creator")
        {
            if (task.TaskStatus == "RunnerPaid" ||
                task.EscrowStatus == "released" ||
                task.EscrowStatus == "refunded")
                return Conflict(new ApiResponse<bool> { Success = false, Message = "This task can no longer be refunded." });

            var activePayout = await _context.Payouts.AnyAsync(p =>
                p.TaskId == task.Id &&
                (p.Status == "Processing" || p.Status == "Completed"));
            if (activePayout)
                return Conflict(new ApiResponse<bool> { Success = false, Message = "A runner payout is already processing or completed for this task." });

            var paymentVerification = await _context.AuditLogs
                .Where(a =>
                    a.EntityType == "Task" &&
                    a.EntityId == task.Id &&
                    a.Action == "PaystackPaymentVerified" &&
                    a.NewValues != null)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync();

            var transactionId = ExtractAuditValue(paymentVerification?.NewValues, "transactionId");
            if (string.IsNullOrWhiteSpace(transactionId))
                return Conflict(new ApiResponse<bool> { Success = false, Message = "Original Paystack transaction ID is not available; refund cannot be safely submitted." });

            var existingRefund = await _context.Refunds
                .FirstOrDefaultAsync(r => r.TaskId == task.Id);

            if (existingRefund != null && existingRefund.Status == "Complete")
                return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "The refund has already completed." });

            if (existingRefund != null && !string.IsNullOrWhiteSpace(existingRefund.RefundId) &&
                existingRefund.Status is "Pending" or "Submitted" or "PendingInvestigation")
                return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "A refund is already submitted and awaiting Paystack confirmation." });

            // Create the unique refund record before calling Paystack. TaskId is unique,
            // so concurrent admin requests cannot create two DFY refund records.
            var refundRecord = existingRefund ?? new Models.Refund
            {
                TaskId = task.Id,
                RefundId = string.Empty,
                TransactionId = transactionId,
                Amount = task.Budget,
                Reason = resolution,
                Status = "Pending",
                Provider = "Paystack"
            };

            if (existingRefund == null)
            {
                _context.Refunds.Add(refundRecord);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    refundRecord = await _context.Refunds.FirstAsync(r => r.TaskId == task.Id);
                    if (!string.IsNullOrWhiteSpace(refundRecord.RefundId))
                        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "A refund is already submitted and awaiting Paystack confirmation." });
                }
            }

            // If a previous attempt has a provider record, reconcile it before resubmitting.
            var providerRefunds = await _paymentProvider.GetRefundsByTransactionIdAsync(
                refundRecord.TransactionId,
                HttpContext.RequestAborted);
            var matchingProviderRefund = providerRefunds
                .Where(r => r.Amount == refundRecord.Amount)
                .OrderByDescending(r => r.Status == 1)
                .FirstOrDefault();

            if (matchingProviderRefund != null)
            {
                refundRecord.RefundId = matchingProviderRefund.RefundId;
                refundRecord.Status = matchingProviderRefund.Status switch
                {
                    1 => "Complete",
                    3 => "Failed",
                    4 => "Cancelled",
                    5 => "Returned",
                    -1 => "Invalid",
                    -2 => "PendingInvestigation",
                    -3 => "Error",
                    _ => "Submitted"
                };
                refundRecord.LastReconciledAt = DateTime.UtcNow;
                if (matchingProviderRefund.Status == 1)
                    refundRecord.CompletedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                if (matchingProviderRefund.Status == 1)
                    return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "The refund was confirmed with Paystack." });

                if (matchingProviderRefund.Status is 3 or 4 or 5 or -1 or -3)
                    return Conflict(new ApiResponse<bool> { Success = false, Message = $"Paystack reports the existing refund as {refundRecord.Status}." });

                return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "A refund is already with Paystack and awaiting completion." });
            }

            var refund = await _paymentProvider.CreateRefundAsync(
                refundRecord.TransactionId,
                refundRecord.Amount,
                refundRecord.Reason,
                HttpContext.RequestAborted);

            if (!refund.Success || string.IsNullOrWhiteSpace(refund.RefundId))
            {
                refundRecord.Status = "Failed";
                refundRecord.FailureReason = refund.Error;
                refundRecord.LastReconciledAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return StatusCode(StatusCodes.Status502BadGateway,
                    new ApiResponse<bool> { Success = false, Message = refund.Error ?? "Unable to submit Paystack refund." });
            }

            refundRecord.RefundId = refund.RefundId;
            refundRecord.Status = "Submitted";
            refundRecord.SubmittedAt = DateTime.UtcNow;
            refundRecord.FailureReason = null;
            await _context.SaveChangesAsync();

            task.PaymentStatus = "RefundPending";
            task.TaskStatus = "RefundPending";
            task.EscrowStatus = "refund_pending";
            task.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _context.AuditLogs.AddAsync(new Models.AuditLog
            {
                UserId = GetCurrentUserId(),
                Action = "PaystackRefundSubmitted",
                EntityType = "Task",
                EntityId = task.Id,
                NewValues = $"refundId={refund.RefundId}; transactionId={transactionId}; amount={task.Budget:F2}",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                UserAgent = Request.Headers.UserAgent.ToString()
            });
            await _context.SaveChangesAsync();

            dispute.Status = "RefundPending";
            dispute.Resolution = resolution;
            dispute.ResolvedAt = null;
            await _context.SaveChangesAsync();

            await _notificationService.CreateNotificationAsync(
                task.CreatedByUserId,
                "refund_pending",
                "Refund Submitted",
                $"Your refund for task {task.TaskId} has been submitted to Paystack and is awaiting confirmation.",
                task.Id);

            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Refund submitted to Paystack and is awaiting provider confirmation." });
        }
        else
        {
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "Unsupported dispute resolution action" });
        }

        dispute.Status = "Resolved";
        dispute.Resolution = resolution;
        dispute.ResolvedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Notify the participants about the administrative resolution.
        var message = action == "release_to_runner"
            ? $"Your dispute for task {task.TaskId} was resolved and the runner payout is being processed through Paystack."
            : $"Your dispute for task {task.TaskId} was resolved.";

        await _notificationService.CreateNotificationAsync(
            task.CreatedByUserId,
            "dispute_resolved",
            "Dispute Resolved",
            message,
            task.Id);

        await _context.AuditLogs.AddAsync(new Models.AuditLog
        {
            UserId = GetCurrentUserId(),
            Action = "ResolveDispute",
            EntityType = "Dispute",
            EntityId = dispute.Id,
            NewValues = $"action={action}; resolution={resolution}; reason={reason}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();

        if (task.AcceptedByUserId.HasValue)
        {
            await _notificationService.CreateNotificationAsync(
                task.AcceptedByUserId.Value,
                "dispute_resolved",
                "Dispute Resolved",
                message,
                task.Id);
        }

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Dispute resolved" });
    }

    private static string? ExtractAuditValue(string? values, string key)
    {
        if (string.IsNullOrWhiteSpace(values)) return null;
        var prefix = key + "=";
        var start = values.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return null;
        start += prefix.Length;
        var end = values.IndexOf(';', start);
        return (end >= 0 ? values[start..end] : values[start..]).Trim();
    }

    private int? GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(claim, out var id) ? id : null;
    }
}
