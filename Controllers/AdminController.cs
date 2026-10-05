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
[Route("api/v1/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IEscrowService _escrowService;
    private readonly IUserPolicyService _userPolicyService;
    private readonly IOzowPaymentService _ozowPaymentService;

    public AdminController(AppDbContext context, IEscrowService escrowService, IUserPolicyService userPolicyService, IOzowPaymentService ozowPaymentService)
    {
        _context = context;
        _escrowService = escrowService;
        _userPolicyService = userPolicyService;
        _ozowPaymentService = ozowPaymentService;
    }

    private int? GetAdminId() =>
        int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private async System.Threading.Tasks.Task WriteAuditAsync(string action, string entityType, int? entityId, string? oldVal = null, string? newVal = null)
    {
        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = GetAdminId(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValues = oldVal,
            NewValues = newVal,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });
        await _context.SaveChangesAsync();
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<object>>> GetDashboardStats()
    {
        var totalUsers = await _context.Users.CountAsync();
        var totalTasks = await _context.Tasks.CountAsync();
        var pendingTasks = await _context.Tasks.CountAsync(t => t.TaskStatus == "PendingPayment");
        var activeTasks = await _context.Tasks.CountAsync(t => t.TaskStatus == "Posted");
        var completedTasks = await _context.Tasks.CountAsync(t => t.TaskStatus == "Completed");
        var totalRevenue = await _context.Tasks
            .Where(t => t.PaymentStatus == "Completed" || t.PaymentStatus == "EscrowHeld")
            .SumAsync(t => t.Budget);

        var platformEarnings = await _context.Tasks
            .Where(t => t.PaymentStatus == "Completed" || t.PaymentStatus == "EscrowHeld")
            .SumAsync(t => t.CommissionAmount);

        var openDisputes = await _context.Disputes.CountAsync(d => d.Status == "Open");

        var recentTasks = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .OrderByDescending(t => t.CreatedAt)
            .Take(5)
            .Select(t => new
            {
                taskId = t.TaskId,
                description = t.TaskDescription,
                userName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                budget = t.Budget,
                status = t.TaskStatus,
                createdAt = t.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                totalUsers,
                totalTasks,
                pendingTasks,
                activeTasks,
                completedTasks,
                totalRevenue,
                platformEarnings,
                grossVolume = totalRevenue,
                openDisputes,
                recentTasks
            }
        });
    }

    [HttpGet("tasks")]
    public async Task<ActionResult<ApiResponse<PaginatedResponse<TaskDto>>>> GetAllTasks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? taskStatus = null,
        [FromQuery] string? paymentStatus = null,
        [FromQuery] string? priority = null,
        [FromQuery] string? search = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _context.Tasks.Include(t => t.CreatedByUser).AsQueryable();

        var resolvedTaskStatus = taskStatus ?? status;
        if (!string.IsNullOrEmpty(resolvedTaskStatus))
            query = query.Where(t => t.TaskStatus == resolvedTaskStatus);

        if (!string.IsNullOrEmpty(paymentStatus))
            query = query.Where(t => t.PaymentStatus == paymentStatus);

        if (!string.IsNullOrEmpty(priority))
            query = query.Where(t => t.Priority == priority);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(t => t.TaskDescription.Contains(search) || t.TaskId.Contains(search));

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var tasks = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TaskDto
            {
                Id = t.Id,
                TaskId = t.TaskId,
                UserName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                UserContact = string.Empty,
                CreatedByUserId = t.CreatedByUserId,
                TaskDescription = t.TaskDescription,
                Category = t.Category,
                Area = t.Area,
                DateNeeded = t.DateNeeded,
                Budget = t.Budget,
                Notes = t.Notes,
                PaymentStatus = t.PaymentStatus,
                TaskStatus = t.TaskStatus,
                HelperName = t.HelperName,
                HelperContact = t.HelperContact,
                Priority = t.Priority,
                CreatedAt = t.CreatedAt,
                CompletedAt = t.CompletedAt
            })
            .ToListAsync();

        var response = new PaginatedResponse<TaskDto>
        {
            Success = true,
            Count = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            Tasks = tasks
        };

        return Ok(new ApiResponse<PaginatedResponse<TaskDto>>
        {
            Success = true,
            Data = response
        });
    }

    [HttpPatch("tasks/{taskId}/verify")]
    public async Task<ActionResult<ApiResponse<bool>>> VerifyTask(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Task not found" });

        task.PaymentStatus = "EscrowHeld";
        task.TaskStatus = "Posted";
        task.EscrowStatus = "held";
        task.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        await WriteAuditAsync("VerifyPayment", "Task", task.Id, "PendingPayment", $"EscrowHeld; reason={request.Reason.Trim()}");

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Task verified successfully"
        });
    }

    [HttpPatch("tasks/{taskId}/unverify")]
    public async Task<ActionResult<ApiResponse<bool>>> UnverifyTask(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Task not found" });

        task.PaymentStatus = "Pending";
        task.TaskStatus = "PendingPayment";
        task.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        await WriteAuditAsync("UnverifyPayment", "Task", task.Id, "Completed", $"Pending; reason={request.Reason.Trim()}");

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Task unverified successfully"
        });
    }

    [HttpGet("payments")]
    public async Task<ActionResult<ApiResponse<object>>> GetPaymentsOverview()
    {
        var paidStatuses = new[] { "EscrowHeld", "Completed", "RunnerPaid" };

        var totalRevenue = await _context.Tasks
            .Where(t => paidStatuses.Contains(t.PaymentStatus))
            .SumAsync(t => t.Budget);

        var platformRevenue = await _context.Tasks
            .Where(t => paidStatuses.Contains(t.PaymentStatus))
            .SumAsync(t => t.CommissionAmount);

        var helperPayouts = await _context.Tasks
            .Where(t => t.TaskStatus == "RunnerPaid")
            .SumAsync(t => t.PayoutAmount);

        var pendingRevenue = await _context.Tasks
            .Where(t => t.PaymentStatus == "Pending" && t.TaskStatus == "PendingPayment")
            .SumAsync(t => t.Budget);

        var pendingPayouts = await _context.Tasks
            .Where(t => t.TaskStatus == "Completed" && t.EscrowStatus == "held")
            .SumAsync(t => t.PayoutAmount);

        var totalRefunded = await _context.Refunds
            .Where(r => r.Status == "Complete")
            .SumAsync(r => r.Amount);

        var recentPayments = await _context.Tasks
            .Include(t => t.CreatedByUser)
            .Include(t => t.AcceptedByUser)
            .Where(t => paidStatuses.Contains(t.PaymentStatus))
            .OrderByDescending(t => t.UpdatedAt)
            .Take(10)
            .Select(t => new
            {
                taskId = t.TaskId,
                taskName = t.TaskDescription,
                amount = t.Budget,
                commission = t.CommissionAmount,
                payout = t.PayoutAmount,
                status = t.PaymentStatus,
                taskStatus = t.TaskStatus,
                userName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                runnerName = t.AcceptedByUser != null ? $"{t.AcceptedByUser.FirstName} {t.AcceptedByUser.LastName}" : null,
                runnerContact = t.AcceptedByUser != null ? t.AcceptedByUser.PhoneNumber : null,
                date = t.UpdatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                totalRevenue,
                platformEarnings = platformRevenue,
                grossVolume = totalRevenue,
                platformRevenue,
                helperPayouts,
                pendingRevenue,
                pendingCommission = pendingRevenue,
                pendingPayouts,
                totalRefunded,
                netRevenue = totalRevenue - totalRefunded,
                recentPayments
            }
        });
    }

    [HttpGet("users")]
    public async Task<ActionResult<ApiResponse<object>>> GetUsers([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var totalCount = await _context.Users.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var userRows = await _context.Users
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var users = userRows.Select(u => new
        {
            id = u.Id,
            name = $"{u.FirstName} {u.LastName}",
            email = u.Email,
            contact = u.PhoneNumber == null ? null : "***",
            role = u.Roles,
            isVerified = u.IsVerified,
            profileCompleted = _userPolicyService.IsProfileComplete(u),
            profileCompletion = _userPolicyService.GetProfileCompletion(u),
            missingProfileFields = _userPolicyService.GetMissingProfileFields(u),
            canCreateTasks = _userPolicyService.CanCreateTasks(u),
            canAcceptTasks = _userPolicyService.CanAcceptTasks(u),
            tasksPosted = _context.Tasks.Count(t => t.CreatedByUserId == u.Id),
            tasksCompleted = u.CompletedTasks,
            rating = u.Rating,
            lastLoginAt = u.LastLoginAt,
            createdAt = u.CreatedAt
        }).ToList();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                users,
                totalCount,
                page,
                pageSize,
                totalPages
            }
        });
    }

    [HttpPatch("users/{userId}/status")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserStatus(int userId, [FromBody] UpdateUserStatusRequest request)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        var old = user.IsVerified;
        user.IsVerified = request.IsVerified;
        await _context.SaveChangesAsync();
        await WriteAuditAsync(request.IsVerified ? "VerifyUser" : "UnverifyUser", "User", userId, old.ToString(), $"{request.IsVerified}; reason={request.Reason.Trim()}");

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User status updated" });
    }

    [HttpPatch("users/{userId}/role")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateUserRole(int userId, [FromBody] JsonElement request)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        var role = request.TryGetProperty("role", out var roleElement)
            ? roleElement.GetString()?.Trim()
            : null;
        var reason = request.TryGetProperty("reason", out var reasonElement)
            ? reasonElement.GetString()?.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(role) ||
            (role != "User" && role != "Admin" && role != "Admin,User"))
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "A valid role is required." });

        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 5 || reason.Length > 500)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "A reason between 5 and 500 characters is required." });

        var oldRole = user.Roles;
        if (string.Equals(oldRole, role, StringComparison.Ordinal))
            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User role unchanged." });

        user.Roles = role;
        await _context.SaveChangesAsync();
        await WriteAuditAsync("UpdateUserRole", "User", userId, oldRole, $"{role}; reason={reason}");

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User role updated" });
    }

    [HttpGet("users/{userId}/tasks")]
    public async Task<ActionResult<ApiResponse<object>>> GetUserTaskHistory(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound(new ApiResponse<object> { Success = false, Message = "User not found" });

        var postedTasks = await _context.Tasks
            .Where(t => t.CreatedByUserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { taskId = t.TaskId, description = t.TaskDescription, taskStatus = t.TaskStatus, budget = t.Budget, createdAt = t.CreatedAt })
            .ToListAsync();

        var acceptedTasks = await _context.Tasks
            .Where(t => t.AcceptedByUserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { taskId = t.TaskId, description = t.TaskDescription, taskStatus = t.TaskStatus, payout = t.PayoutAmount, createdAt = t.CreatedAt })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                user = new { user.Id, user.Rating, user.CompletedTasks },
                postedTasks,
                acceptedTasks
            }
        });
    }

    [HttpPatch("tasks/bulk-verify")]
    public async Task<ActionResult<ApiResponse<bool>>> BulkVerifyTasks([FromBody] BulkVerifyRequest request)
    {
        var tasks = await _context.Tasks
            .Where(t => request.TaskIds.Contains(t.TaskId))
            .ToListAsync();

        foreach (var task in tasks)
        {
            task.PaymentStatus = "EscrowHeld";
            task.TaskStatus = "Posted";
            task.EscrowStatus = "held";
            task.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        foreach (var task in tasks)
            await WriteAuditAsync("BulkVerifyPayment", "Task", task.Id, "PendingPayment", $"EscrowHeld; reason={request.Reason.Trim()}");

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = $"{tasks.Count} tasks verified" });
    }


    [HttpPatch("tasks/{taskId}/expire-confirmation")]
    public async Task<ActionResult<ApiResponse<bool>>> ExpireConfirmation(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DFY_E2E_TEST_MODE"), "true", StringComparison.OrdinalIgnoreCase))
            return NotFound();

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Data = false, Message = "Task not found" });

        if (task.TaskStatus != "AwaitingConfirmation" ||
            task.EscrowStatus != "held" ||
            task.PaymentStatus != "EscrowHeld")
        {
            return Conflict(new ApiResponse<bool> { Success = false, Data = false, Message = "Task is not awaiting creator confirmation." });
        }

        // In E2E mode only, seed the minimum verified payout prerequisite so the
        // production escrow release path can be exercised without an external bank/Ozow call.
        if (task.AcceptedByUserId.HasValue)
        {
            var testBankAccount = await _context.BankAccounts
                .FirstOrDefaultAsync(b => b.UserId == task.AcceptedByUserId.Value && b.IsActive && b.IsVerified);

            if (testBankAccount == null)
            {
                testBankAccount = new Models.BankAccount
                {
                    UserId = task.AcceptedByUserId.Value,
                    BankName = "DFY E2E Test Bank",
                    BankGroupId = "DFY-E2E",
                    AccountNumber = "0000000000",
                    AccountHolderName = task.AcceptedByUser?.FirstName + " " + task.AcceptedByUser?.LastName,
                    BranchCode = "000000",
                    AccountType = "Savings",
                    IsVerified = true,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    VerifiedAt = DateTime.UtcNow
                };
                _context.BankAccounts.Add(testBankAccount);
                await _context.SaveChangesAsync();
            }
        }

        // Test/operations harness: backdate the same 24-hour deadline used by
        // normal completion, then run the production auto-release service path.
        task.EscrowHoldUntil = DateTime.UtcNow.AddMinutes(-1);
        task.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var released = await _escrowService.AutoReleaseExpiredEscrowsAsync();
        await WriteAuditAsync(
            "ExpireCreatorConfirmation",
            "Task",
            task.Id,
            "AwaitingConfirmation",
            $"AutoReleaseExpiredEscrows; released={released}; reason={request.Reason.Trim()}");

        return Ok(new ApiResponse<bool>
        {
            Success = released > 0,
            Data = released > 0,
            Message = released > 0
                ? "Creator confirmation expired and the runner payout was queued."
                : "Creator confirmation was expired, but the escrow could not be released."
        });
    }

    [HttpPatch("tasks/{taskId}/force-release-escrow")]
    public async Task<ActionResult<ApiResponse<bool>>> ForceReleaseEscrow(string taskId, [FromBody] AdminActionReasonRequest request)
    {
        var task = await _context.Tasks
            .FirstOrDefaultAsync(t => t.TaskId == taskId);

        if (task == null)
        {
            return NotFound(new ApiResponse<bool>
            {
                Success = false,
                Data = false,
                Message = "Task not found"
            });
        }

        if (task.AcceptedByUserId == null)
        {
            return Ok(new ApiResponse<bool>
            {
                Success = false,
                Data = false,
                Message = "Task has no assigned runner."
            });
        }

        if (task.EscrowStatus != "held" &&
            task.PaymentStatus != "EscrowHeld")
        {
            return Ok(new ApiResponse<bool>
            {
                Success = false,
                Data = false,
                Message = "Task is not eligible for escrow release."
            });
        }

        // IMPORTANT:
        // Admin release must enter the same direct-to-bank payout
        // pipeline as normal confirmation. It must NEVER set
        // RunnerPaid directly.
        var released = await _escrowService.ReleaseEscrowAsync(
            task.Id,
            force: true);

        if (!released)
        {
            return Ok(new ApiResponse<bool>
            {
                Success = false,
                Data = false,
                Message = "Unable to initiate runner payout."
            });
        }

        await WriteAuditAsync(
            "ForceReleaseEscrow",
            "Task",
            task.Id,
            null,
            $"PayoutPending; reason={request.Reason.Trim()}");

        return Ok(new ApiResponse<bool>
        {
            Success = true,
            Data = true,
            Message = "Escrow released and runner payout queued for processing."
        });
    }


    [HttpDelete("tasks/{taskId}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteTask(string taskId)
    {
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Task not found" });

        var hasFinancialActivity =
            task.PaymentStatus != "Pending" ||
            task.EscrowStatus != "pending" ||
            task.PayoutReference != null ||
            task.PayoutInitiatedAt.HasValue ||
            task.PayoutCompletedAt.HasValue ||
            task.PaidToRunnerAt.HasValue ||
            task.AcceptedByUserId.HasValue ||
            await _context.Payouts.AnyAsync(p => p.TaskId == task.Id);

        if (hasFinancialActivity)
            return Conflict(new ApiResponse<bool> { Success = false, Data = false, Message = "Financially active tasks cannot be hard-deleted. Retain the record for audit and financial reconciliation." });

        task.IsDeleted = true;
        task.DeletedAt = DateTime.UtcNow;
        task.TaskStatus = "Cancelled";
        task.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await WriteAuditAsync("SoftDeleteTask", "Task", task.Id, "Active", "Cancelled; admin deletion requested");

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Task archived" });
    }

    [HttpDelete("users/{userId}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteUser(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        var hasUserFinancialActivity =
            user.WalletBalance != 0 ||
            await _context.Tasks.AnyAsync(t => t.CreatedByUserId == userId || t.AcceptedByUserId == userId) ||
            await _context.Payouts.AnyAsync(p => p.RunnerId == userId);

        if (hasUserFinancialActivity)
            return Conflict(new ApiResponse<bool> { Success = false, Data = false, Message = "Users with task or financial history cannot be hard-deleted. Deactivate or suspend the account instead." });

        return Conflict(new ApiResponse<bool> { Success = false, Data = false, Message = "User deletion is disabled to preserve audit history." });
    }

    [HttpGet("audit-logs")]
    public async Task<ActionResult<ApiResponse<object>>> GetAuditLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var total = await _context.AuditLogs.CountAsync();
        var logs = await _context.AuditLogs
            .Include(a => a.User)
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                id = a.Id,
                userId = a.UserId,
                adminName = a.User != null ? $"{a.User.FirstName} {a.User.LastName}" : "System",
                userName = a.User != null ? $"{a.User.FirstName} {a.User.LastName}" : "System",
                action = a.Action,
                entityType = a.EntityType,
                entityId = a.EntityId,
                oldValues = a.OldValues,
                newValues = a.NewValues,
                ipAddress = a.IpAddress,
                createdAt = a.CreatedAt
            })
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(total / (double)pageSize);

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { logs, total, totalCount = total, page, pageSize, totalPages }
        });
    }

    [HttpGet("tasks/{taskId}/messages")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetTaskMessages(string taskId)
    {
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<List<object>> { Success = false, Message = "Task not found" });

        var messages = await _context.TaskMessages
            .Include(m => m.Sender)
            .Where(m => m.TaskId == task.Id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                id = m.Id,
                senderId = m.SenderId,
                senderName = $"{m.Sender.FirstName} {m.Sender.LastName}",
                senderEmail = m.Sender.Email,
                content = m.Content,
                timestamp = m.CreatedAt,
                isRead = m.IsRead
            })
            .Cast<object>()
            .ToListAsync();

        return Ok(new ApiResponse<List<object>> { Success = true, Data = messages });
    }

    [HttpGet("refunds")]
    public async Task<ActionResult<ApiResponse<object>>> GetRefunds([FromQuery] string? status = null)
    {
        var query = _context.Refunds
            .Include(r => r.Task)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => r.Status == status);

        var refunds = await query
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                id = r.Id,
                taskId = r.Task.TaskId,
                refundId = r.RefundId,
                transactionId = r.TransactionId,
                amount = r.Amount,
                reason = r.Reason,
                status = r.Status,
                provider = r.Provider,
                failureReason = r.FailureReason,
                createdAt = r.CreatedAt,
                submittedAt = r.SubmittedAt,
                completedAt = r.CompletedAt,
                lastReconciledAt = r.LastReconciledAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = refunds });
    }

    [HttpPost("refunds/reconcile")]
    public async Task<ActionResult<ApiResponse<object>>> ReconcileRefunds()
    {
        var pending = await _context.Refunds
            .Where(r => r.Status == "Pending" || r.Status == "Submitted" || r.Status == "PendingInvestigation")
            .ToListAsync();

        var reconciled = 0;
        foreach (var refund in pending)
        {
            var providerRefunds = await _ozowPaymentService.GetRefundsByTransactionIdAsync(
                refund.TransactionId,
                HttpContext.RequestAborted);

            var match = providerRefunds
                .Where(r => r.Amount == refund.Amount)
                .OrderByDescending(r => r.Status == 1)
                .FirstOrDefault();

            if (match == null)
            {
                refund.LastReconciledAt = DateTime.UtcNow;
                continue;
            }

            refund.RefundId = match.RefundId;
            refund.Status = match.Status switch
            {
                0 => "Pending",
                1 => "Complete",
                2 => "Submitted",
                3 => "Failed",
                4 => "Cancelled",
                5 => "Returned",
                -1 => "Invalid",
                -2 => "PendingInvestigation",
                -3 => "Error",
                _ => "Pending"
            };
            refund.LastReconciledAt = DateTime.UtcNow;
            if (match.Status == 1)
                refund.CompletedAt ??= DateTime.UtcNow;

            var task = await _context.Tasks.FirstOrDefaultAsync(t => t.Id == refund.TaskId);
            if (task != null && match.Status == 1)
            {
                task.PaymentStatus = "Refunded";
                task.TaskStatus = "Cancelled";
                task.EscrowStatus = "refunded";
                task.PayoutStatus = "NotStarted";
                task.PayoutReference = null;
                task.PayoutInitiatedAt = null;
                task.PayoutCompletedAt = null;
                task.PaidToRunnerAt = null;
                task.UpdatedAt = DateTime.UtcNow;
            }
            reconciled++;
        }

        await _context.SaveChangesAsync();
        await WriteAuditAsync("ReconcileRefunds", "Refund", null, null, $"reconciled={reconciled}");

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { scanned = pending.Count, reconciled },
            Message = "Refund reconciliation completed."
        });
    }

    [HttpGet("withdrawal-requests")]
    public async Task<ActionResult<ApiResponse<object>>> GetWithdrawalRequests([FromQuery] string? status = null)
    {
        var query = _context.WithdrawalRequests
            .Include(w => w.User)
            .Include(w => w.BankAccount)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(w => w.Status == status);

        var requests = await query
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new
            {
                id = w.Id,
                userId = w.UserId,
                userName = $"{w.User.FirstName} {w.User.LastName}",
                amount = w.Amount,
                fee = w.Fee,
                status = w.Status,
                reference = w.Reference,
                bankName = w.BankAccount.BankName,
                accountNumber = "****",
                createdAt = w.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = requests });
    }

    [HttpGet("bank-accounts")]
    public async Task<ActionResult<ApiResponse<object>>> GetBankAccounts([FromQuery] bool? unverifiedOnly = null)
    {
        var query = _context.BankAccounts.Include(b => b.User).AsQueryable();

        if (unverifiedOnly == true)
            query = query.Where(b => !b.IsVerified);

        var accounts = await query
            .Select(b => new
            {
                id = b.Id,
                userId = b.UserId,
                userName = $"{b.User.FirstName} {b.User.LastName}",
                bankName = b.BankName,
                accountNumber = b.AccountNumber,
                accountHolderName = b.AccountHolderName,
                isVerified = b.IsVerified,
                createdAt = b.CreatedAt
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Success = true, Data = accounts });
    }

    [HttpPatch("bank-accounts/{id}/verify")]
    public async Task<ActionResult<ApiResponse<bool>>> VerifyBankAccount(int id)
    {
        var account = await _context.BankAccounts.FindAsync(id);
        if (account == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Bank account not found" });

        account.IsVerified = true;
        account.VerifiedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await WriteAuditAsync("VerifyBankAccount", "BankAccount", id, "Unverified", "Verified");

        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Bank account verified" });
    }
}