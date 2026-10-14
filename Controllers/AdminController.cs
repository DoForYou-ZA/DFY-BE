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
    private readonly IPaymentProvider _paymentProvider;
    private readonly IBankingService _bankingService;
    private readonly IPaystackSettlementProvider _paystackSettlementProvider;

    public AdminController(AppDbContext context, IEscrowService escrowService, IUserPolicyService userPolicyService, IPaymentProvider paymentProvider, IBankingService bankingService, IPaystackSettlementProvider paystackSettlementProvider)
    {
        _context = context;
        _escrowService = escrowService;
        _userPolicyService = userPolicyService;
        _paymentProvider = paymentProvider;
        _bankingService = bankingService;
        _paystackSettlementProvider = paystackSettlementProvider;
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

    /// <summary>
    /// Retrieves Paystack settlement batches and their transactions, then compares transaction
    /// references and amounts with DFY's persisted payment records. Read-only; no money moves.
    /// </summary>
    [HttpGet("financial-reconciliation/paystack-settlements")]
    public async Task<ActionResult<ApiResponse<object>>> GetPaystackSettlementReconciliation(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int maxSettlements = 25,
        CancellationToken cancellationToken = default)
    {
        var end = to?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var start = from?.ToUniversalTime() ?? end.AddDays(-30);
        maxSettlements = Math.Clamp(maxSettlements, 1, 50);

        if (end > DateTimeOffset.UtcNow.AddMinutes(5))
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = "The settlement end date cannot be in the future."
            });
        if (start > end)
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = "The settlement start date must be on or before the end date."
            });
        if ((end - start).TotalDays > 90)
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = "Use a date range of 90 days or less; run separate reports for longer periods."
            });

        var providerResult = await _paystackSettlementProvider.GetSettlementsAsync(
            start, end, maxSettlements, cancellationToken);

        if (!providerResult.Success)
            return StatusCode(StatusCodes.Status502BadGateway, new ApiResponse<object>
            {
                Success = false,
                Message = providerResult.Error ?? "Paystack settlement records could not be retrieved.",
                Data = new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    fromUtc = start,
                    toUtc = end,
                    provider = "Paystack",
                    providerRecordsRetrieved = false
                }
            });

        var references = providerResult.Transactions
            .Select(t => t.Reference)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct()
            .ToArray();

        var attempts = references.Length == 0
            ? new List<Models.PaymentAttempt>()
            : await _context.PaymentAttempts.AsNoTracking()
                .Where(p => p.Provider == "Paystack" && references.Contains(p.Reference))
                .ToListAsync(cancellationToken);

        var attemptByReference = attempts
            .GroupBy(p => p.Reference, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var taskIds = attempts.Select(p => p.TaskId).Distinct().ToArray();
        var tasks = taskIds.Length == 0
            ? new List<Models.Task>()
            : await _context.Tasks.AsNoTracking()
                .Where(t => taskIds.Contains(t.Id))
                .ToListAsync(cancellationToken);

        var taskById = tasks.ToDictionary(t => t.Id);
        var payments = taskIds.Length == 0
            ? new List<Models.Payment>()
            : await _context.Payments.AsNoTracking()
                .Where(p => taskIds.Contains(p.TaskId) && p.Provider == "Paystack")
                .ToListAsync(cancellationToken);

        var transactionRows = providerResult.Transactions.Select(transaction =>
        {
            attemptByReference.TryGetValue(transaction.Reference, out var matchingAttempts);
            matchingAttempts ??= Array.Empty<Models.PaymentAttempt>();
            var matchingTaskIds = matchingAttempts.Select(a => a.TaskId).Distinct().ToArray();
            var matchingTasks = matchingTaskIds.Where(taskById.ContainsKey).Select(id => taskById[id]).ToArray();
            var matchingPayments = payments.Where(p =>
                matchingTaskIds.Contains(p.TaskId) &&
                matchingAttempts.Any(a => a.TaskId == p.TaskId && a.Reference == transaction.Reference)).ToArray();
            var findings = new List<string>();

            if (matchingAttempts.Length == 0)
                findings.Add("Paystack settlement transaction has no matching DFY payment attempt.");
            if (matchingAttempts.Length > 1)
                findings.Add("Multiple DFY payment attempts share this Paystack reference.");
            if (matchingAttempts.Any(a => a.Amount != transaction.Amount))
                findings.Add("Paystack transaction amount differs from the DFY payment-attempt amount.");
            if (matchingTasks.Any(t => t.Budget != transaction.Amount))
                findings.Add("Paystack transaction amount differs from the persisted task budget.");
            if (matchingPayments.Any(p => p.Amount != transaction.Amount))
                findings.Add("Paystack transaction amount differs from a persisted DFY payment record.");

            return new
            {
                transaction.SettlementId,
                transaction.Reference,
                transaction.TransactionId,
                transaction.Status,
                transaction.Currency,
                providerAmount = transaction.Amount,
                providerFees = transaction.Fees,
                transaction.PaidAt,
                matchedTaskIds = matchingTasks.Select(t => t.TaskId),
                matchingAttemptCount = matchingAttempts.Length,
                localAttemptAmounts = matchingAttempts.Select(a => a.Amount),
                localPaymentAmounts = matchingPayments.Select(p => p.Amount),
                findings,
                isConsistent = findings.Count == 0
            };
        }).ToList();

        var providerReferences = new HashSet<string>(references, StringComparer.Ordinal);
        var missingLocalAttempts = await _context.PaymentAttempts.AsNoTracking()
            .Where(p => p.Provider == "Paystack" &&
                p.CreatedAt >= start.UtcDateTime &&
                p.CreatedAt <= end.UtcDateTime &&
                (p.Status == "Completed" || p.Status == "Success" || p.Status == "Paid"))
            .Select(p => new { p.Reference, p.Amount, p.Status, p.CreatedAt, p.TaskId })
            .ToListAsync(cancellationToken);
        var localSuccessfulAttemptsMissingFromSettlements = missingLocalAttempts
            .Where(p => !providerReferences.Contains(p.Reference))
            .Select(p => new { p.Reference, p.Amount, p.Status, p.CreatedAt, p.TaskId })
            .ToList();

        var settlementSummary = providerResult.Settlements.Select(s => new
        {
            s.Id,
            s.Status,
            s.Currency,
            s.Domain,
            grossProcessed = s.TotalProcessed,
            settlementAmount = s.TotalAmount,
            effectiveAmount = s.EffectiveAmount,
            providerFees = s.TotalFees,
            s.SettlementDate
        }).ToList();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Message = providerResult.IsComplete
                ? "Paystack settlement records were retrieved and compared with DFY payment records. Review findings before any corrective action."
                : "Partial Paystack settlement data was retrieved. Narrow the date window and rerun before treating this report as complete.",
            Data = new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                fromUtc = start,
                toUtc = end,
                provider = "Paystack",
                providerDomain = providerResult.Settlements.Select(s => s.Domain).Distinct(),
                providerRecordsRetrieved = true,
                reconciliationComplete = providerResult.IsComplete,
                providerError = providerResult.Error,
                settlementCount = settlementSummary.Count,
                settlementGrossProcessed = settlementSummary.Sum(s => s.grossProcessed),
                settlementAmount = settlementSummary.Sum(s => s.settlementAmount),
                settlementEffectiveAmount = settlementSummary.Sum(s => s.effectiveAmount),
                settlementFees = settlementSummary.Sum(s => s.providerFees),
                transactionCount = transactionRows.Count,
                transactionFindings = transactionRows.Count(t => !t.isConsistent),
                localSuccessfulAttemptsMissingFromSettlements,
                settlements = settlementSummary,
                transactions = transactionRows,
                note = "Settlement batches represent funds Paystack sends to the merchant settlement account. This report does not reconcile runner transfer fees, chargebacks outside the returned date window, bank-statement credits, or legal escrow/safeguarding status. It never changes financial state."
            }
        });
    }

    /// <summary>
    /// Read-only DFY ledger consistency report for paid/escrowed tasks.
    /// This endpoint never calls a payment provider, changes financial state, or triggers payouts.
    /// Provider settlement reconciliation must be performed separately against Paystack records.
    /// </summary>
    [HttpGet("financial-reconciliation")]
    public async Task<ActionResult<ApiResponse<object>>> GetFinancialReconciliation(
        [FromQuery] int days = 30,
        [FromQuery] int limit = 250)
    {
        days = Math.Clamp(days, 1, 90);
        limit = Math.Clamp(limit, 1, 500);
        var since = DateTime.UtcNow.AddDays(-days);

        var tasks = await _context.Tasks
            .AsNoTracking()
            .Where(t => t.UpdatedAt >= since &&
                (t.PaymentStatus == "Completed" ||
                 t.PaymentStatus == "EscrowHeld" ||
                 t.TaskStatus == "PayoutPending" ||
                 t.TaskStatus == "RunnerPaid"))
            .OrderByDescending(t => t.UpdatedAt)
            .Take(limit)
            .Select(t => new
            {
                t.Id, t.TaskId, t.Budget, t.CommissionPercentage,
                t.CommissionAmount, t.PayoutAmount, t.PaymentStatus,
                t.TaskStatus, t.PayoutStatus, t.PaymentReference, t.UpdatedAt
            })
            .ToListAsync();

        var taskIds = tasks.Select(t => t.Id).ToArray();
        var attempts = await _context.PaymentAttempts.AsNoTracking()
            .Where(p => taskIds.Contains(p.TaskId) && p.Provider == "Paystack")
            .Select(p => new { p.TaskId, p.Reference, p.Amount, p.Status, p.TransactionId })
            .ToListAsync();
        var payments = await _context.Payments.AsNoTracking()
            .Where(p => taskIds.Contains(p.TaskId) && p.Provider == "Paystack")
            .Select(p => new { p.TaskId, p.PaymentId, p.Amount, p.Status, p.TransactionId })
            .ToListAsync();
        var payouts = await _context.Payouts.AsNoTracking()
            .Where(p => taskIds.Contains(p.TaskId) && p.Provider == "Paystack")
            .Select(p => new { p.TaskId, p.Amount, p.Status, p.MerchantReference, p.ProviderReference, p.CreatedAt })
            .ToListAsync();
        var refunds = await _context.Refunds.AsNoTracking()
            .Where(r => taskIds.Contains(r.TaskId) && (r.Provider == null || r.Provider == "Paystack"))
            .Select(r => new { r.TaskId, r.Amount, r.Status, r.RefundId })
            .ToListAsync();

        var rows = tasks.Select(task =>
        {
            var expectedCommission = _escrowService.CalculateCommission(task.Budget);
            var expectedRunnerAmount = decimal.Round(
                task.Budget - expectedCommission, 2, MidpointRounding.AwayFromZero);
            var taskAttempts = attempts.Where(p => p.TaskId == task.Id).ToArray();
            var taskPayments = payments.Where(p => p.TaskId == task.Id).ToArray();
            var taskPayouts = payouts.Where(p => p.TaskId == task.Id).ToArray();
            var taskRefunds = refunds.Where(p => p.TaskId == task.Id).ToArray();
            var findings = new List<string>();

            if (task.CommissionAmount != expectedCommission)
                findings.Add("CommissionAmount differs from the backend commission calculation.");
            if (task.PayoutAmount != expectedRunnerAmount)
                findings.Add("PayoutAmount differs from budget minus backend commission.");
            if (taskAttempts.Any(p => p.Amount != task.Budget))
                findings.Add("At least one Paystack payment attempt amount differs from the task budget.");
            if (taskPayments.Any(p => p.Amount != task.Budget))
                findings.Add("At least one persisted Paystack payment amount differs from the task budget.");
            if (taskPayouts.Any(p => p.Amount != task.PayoutAmount))
                findings.Add("At least one Paystack payout amount differs from the persisted runner amount.");
            if (taskPayouts.Count(p => p.Status is "Pending" or "Processing" or "Queued" or "Initiated") > 1)
                findings.Add("Multiple non-terminal Paystack payout records exist for this task.");
            if (taskRefunds.Any(r => r.Status is "Complete" or "Completed" or "Processed") &&
                taskPayouts.Any(p => p.Status is "Pending" or "Processing" or "Queued" or "Initiated"))
                findings.Add("A completed refund coexists with a pending/processing payout; manual review required.");

            return new
            {
                taskId = task.TaskId,
                internalTaskId = task.Id,
                task.Budget,
                task.CommissionPercentage,
                persistedCommission = task.CommissionAmount,
                expectedCommission,
                persistedRunnerAmount = task.PayoutAmount,
                expectedRunnerAmount,
                task.PaymentStatus,
                task.TaskStatus,
                task.PayoutStatus,
                task.PaymentReference,
                paymentAttemptCount = taskAttempts.Length,
                paymentAttempts = taskAttempts.Select(p => new { p.Reference, p.Amount, p.Status, p.TransactionId }),
                paymentRecordCount = taskPayments.Length,
                payments = taskPayments.Select(p => new { p.PaymentId, p.Amount, p.Status, p.TransactionId }),
                payoutRecordCount = taskPayouts.Length,
                payouts = taskPayouts.Select(p => new
                {
                    p.Amount, p.Status, p.MerchantReference, p.ProviderReference, p.CreatedAt
                }),
                refunds = taskRefunds.Select(r => new { r.Amount, r.Status, r.RefundId }),
                findings,
                isConsistent = findings.Count == 0,
                task.UpdatedAt
            };
        }).ToList();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Message = "Read-only DFY ledger consistency report. Paystack settlement is not verified and no financial state is changed.",
            Data = new
            {
                generatedAtUtc = DateTime.UtcNow,
                sinceUtc = since,
                days,
                limit,
                returnedTaskCount = rows.Count,
                tasksWithFindings = rows.Count(r => !r.isConsistent),
                findingCount = rows.Sum(r => r.findings.Count),
                providerSettlementVerified = false,
                rows
            }
        });
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<object>>> GetDashboardStats()
    {
        var totalUsers = await _context.Users.CountAsync(u => !u.IsArchived);
        var archivedUsers = await _context.Users.CountAsync(u => u.IsArchived);
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
                archivedUsers,
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
        var task = await _context.Tasks
            .Include(t => t.AcceptedByUser)
            .FirstOrDefaultAsync(t => t.TaskId == taskId);
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
        var task = await _context.Tasks
            .Include(t => t.AcceptedByUser)
            .FirstOrDefaultAsync(t => t.TaskId == taskId);
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
    public async Task<ActionResult<ApiResponse<object>>> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] bool includeArchived = false)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        IQueryable<Models.User> query = _context.Users.AsNoTracking();
        if (!includeArchived)
            query = query.Where(u => !u.IsArchived);

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var userRows = await query
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
            createdAt = u.CreatedAt,
            isArchived = u.IsArchived,
            archivedAt = u.ArchivedAt,
            archivedReason = u.ArchivedReason
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
                totalPages,
                includeArchived
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

        if (task.AcceptedByUserId.HasValue)
        {
            var hasPaystackRecipient = await _context.BankAccounts.AnyAsync(b =>
                b.UserId == task.AcceptedByUserId.Value &&
                b.IsActive &&
                b.IsVerified &&
                !string.IsNullOrWhiteSpace(b.ProviderReference));
            if (!hasPaystackRecipient)
            {
                return Conflict(new ApiResponse<bool>
                {
                    Success = false,
                    Data = false,
                    Error = "PAYSTACK_RECIPIENT_REQUIRED",
                    Message = "E2E escrow release requires the runner's active, Paystack-verified bank account."
                });
            }
        }

        // Test/operations harness: backdate the same 24-hour deadline used by
        // normal completion, then call the exact release operation the background
        // worker uses for each expired task. AutoReleaseExpiredEscrowsAsync is a
        // legacy no-op; EscrowReleaseService owns the expiry query/loop.
        task.EscrowHoldUntil = DateTime.UtcNow.AddMinutes(-1);
        task.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var released = await _escrowService.ReleaseEscrowAsync(task.Id);
        if (!released)
        {
            // The hosted expiry worker may win the same atomic state transition
            // after the deadline is backdated. Treat its completed transition as
            // success rather than reporting a false negative to the E2E caller.
            var currentState = await _context.Tasks
                .AsNoTracking()
                .Where(t => t.Id == task.Id)
                .Select(t => new { t.TaskStatus, t.PaymentStatus, t.EscrowStatus })
                .FirstOrDefaultAsync();
            released = currentState is not null &&
                       currentState.TaskStatus == "PayoutPending" &&
                       currentState.PaymentStatus == "EscrowReleased" &&
                       currentState.EscrowStatus == "released";
        }

        await WriteAuditAsync(
            "ExpireCreatorConfirmation",
            "Task",
            task.Id,
            "AwaitingConfirmation",
            $"AutoReleaseExpiredEscrows; released={released}; reason={request.Reason.Trim()}");

        return Ok(new ApiResponse<bool>
        {
            Success = released,
            Data = released,
            Message = released
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
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        var adminId = GetAdminId();
        if (adminId == userId)
            return Conflict(new ApiResponse<bool> { Success = false, Data = false, Message = "You cannot archive your own administrator account." });

        if (string.Equals(user.UserType, "admin", StringComparison.OrdinalIgnoreCase) ||
            user.Roles?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains("Admin", StringComparer.OrdinalIgnoreCase) == true)
            return Conflict(new ApiResponse<bool> { Success = false, Data = false, Message = "Administrator accounts cannot be archived through user management." });

        if (user.IsArchived)
            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User is already archived." });

        var oldValues = System.Text.Json.JsonSerializer.Serialize(new
        {
            user.IsArchived,
            user.ArchivedAt
        });
        user.IsArchived = true;
        user.ArchivedAt = DateTime.UtcNow;
        user.ArchivedReason = "Archived by an administrator through user management";

        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = adminId,
            Action = "ArchiveUser",
            EntityType = "User",
            EntityId = user.Id,
            OldValues = oldValues,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                user.IsArchived,
                user.ArchivedAt,
                user.ArchivedReason
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });

        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User archived. Audit and task history have been retained." });
    }

    [HttpPost("users/{userId}/restore")]
    public async Task<ActionResult<ApiResponse<bool>>> RestoreUser(int userId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "User not found" });

        if (!user.IsArchived)
            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User is already active." });

        var adminId = GetAdminId();
        var oldValues = System.Text.Json.JsonSerializer.Serialize(new
        {
            user.IsArchived,
            user.ArchivedAt,
            user.ArchivedReason
        });
        user.IsArchived = false;
        user.ArchivedAt = null;
        user.ArchivedReason = null;

        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = adminId,
            Action = "RestoreUser",
            EntityType = "User",
            EntityId = user.Id,
            OldValues = oldValues,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                user.IsArchived,
                user.ArchivedAt,
                user.ArchivedReason
            }),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });

        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "User restored." });
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
            var providerRefunds = await _paymentProvider.GetRefundsByTransactionIdAsync(
                refund.TransactionId,
                HttpContext.RequestAborted);

            var match = providerRefunds
                .Where(r => r.Amount == refund.Amount)
                .OrderByDescending(r => string.Equals(r.Status, "processed", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (match == null)
            {
                refund.LastReconciledAt = DateTime.UtcNow;
                continue;
            }

            refund.RefundId = match.RefundId;
            refund.Status = match.Status.ToLowerInvariant() switch
            {
                "pending" or "processing" => "Submitted",
                "processed" => "Complete",
                "failed" => "Failed",
                "needs-attention" => "PendingInvestigation",
                _ => "Submitted"
            };
            refund.LastReconciledAt = DateTime.UtcNow;
            if (refund.Status == "Complete")
                refund.CompletedAt ??= DateTime.UtcNow;

            var task = await _context.Tasks.FirstOrDefaultAsync(t => t.Id == refund.TaskId);
            if (task != null && refund.Status == "Complete")
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
        var account = await _context.BankAccounts
            .Include(b => b.User)
            .FirstOrDefaultAsync(b => b.Id == id);
        if (account == null)
            return NotFound(new ApiResponse<bool> { Success = false, Message = "Bank account not found" });

        var verification = await _bankingService.VerifyBankAccountAsync(
            account.User,
            account,
            HttpContext.RequestAborted);

        account.IsVerified = verification.Success;
        account.VerifiedAt = verification.Success ? DateTime.UtcNow : null;
        account.ProviderReference = verification.ProviderReference;
        await _context.SaveChangesAsync();
        await WriteAuditAsync(
            verification.Success ? "VerifyBankAccount" : "BankAccountVerificationFailed",
            "BankAccount",
            id,
            null,
            verification.Message);

        return Ok(new ApiResponse<bool>
        {
            Success = verification.Success,
            Data = verification.Success,
            Message = verification.Message
        });
    }
}