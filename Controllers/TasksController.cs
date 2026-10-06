using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using System.Security.Claims;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/tasks")]
[Authorize]
public class TasksController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IRulesEngine _rulesEngine;
    private readonly IEscrowService _escrowService;
    private readonly INotificationService _notificationService;
    private readonly IConfiguration _configuration;
    private readonly IPaymentProvider _paymentProvider;
    private readonly IUserPolicyService _userPolicyService;

    public TasksController(
        AppDbContext context,
        IRulesEngine rulesEngine,
        IEscrowService escrowService,
        INotificationService notificationService,
        IConfiguration configuration,
        IPaymentProvider paymentProvider,
        IUserPolicyService userPolicyService)
    {
        _context = context;
        _rulesEngine = rulesEngine;
        _escrowService = escrowService;
        _notificationService = notificationService;
        _configuration = configuration;
        _paymentProvider = paymentProvider;
        _userPolicyService = userPolicyService;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> CreateTask([FromBody] CreateTaskRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        if (!_userPolicyService.IsProfileComplete(user))
            return Ok(new ApiResponse<object> { Success = false, Message = "Please complete your profile before creating tasks" });

        if (!_userPolicyService.CanCreateTasks(user))
            return StatusCode(StatusCodes.Status403Forbidden,
                new ApiResponse<object> { Success = false, Message = "Only Creators and Both accounts can post tasks." });

        var ruleContext = new RuleContext { CurrentUser = user, Action = "CreateTask" };
        var canCreate = await _rulesEngine.CanPerformActionAsync("User", "CreateTask", ruleContext);
        if (!canCreate)
        {
            var validationResults = await _rulesEngine.ValidateAsync("User", "permission", ruleContext);
            var errorMessage = validationResults.FirstOrDefault(r => !r.IsValid)?.ErrorMessage ?? "You cannot create tasks";
            return Ok(new ApiResponse<object> { Success = false, Message = errorMessage });
        }

        if (request.Budget < 50)
            return Ok(new ApiResponse<object> { Success = false, Message = "validation failed: minimum budget is R50" });

        var taskId = GenerateTaskId();
        var commission = _escrowService.CalculateCommission(request.Budget);
        var payout = request.Budget - commission;

        var task = new Models.Task
        {
            TaskId = taskId,
            TaskName = request.TaskName.Trim(),
            TaskDescription = request.TaskDescription.Trim(),
            Category = request.Category,
            Area = request.Area,
            DateNeeded = DateTime.SpecifyKind(request.DateNeeded, DateTimeKind.Utc),
            Budget = request.Budget,
            Notes = request.Notes,
            Priority = request.Priority,
            CreatedByUserId = userId.Value,
            PaymentStatus = "Pending",
            TaskStatus = "PendingPayment",
            CommissionAmount = commission,
            PayoutAmount = payout,
            EscrowStatus = "pending"
        };

        ruleContext.Entity = task;
        var taskValidation = await _rulesEngine.ValidateEntityAsync(task, ruleContext);
        if (!taskValidation.IsValid)
            return Ok(new ApiResponse<object> { Success = false, Message = taskValidation.ErrorMessage ?? "Task validation failed" });

        _context.Tasks.Add(task);
        await _context.SaveChangesAsync();

        await _notificationService.NotifyAdminsAsync(
            "payment_pending",
            "New Task Awaiting Payment",
            $"{user.FirstName} {user.LastName} created task {taskId} — R{request.Budget:F0} ({request.Category})",
            task.Id);

        task.PaymentReference = GeneratePaymentReference(task.TaskId);
        var payment = await _paymentProvider.CreatePaymentAsync(task, user, HttpContext.RequestAborted);
        if (!payment.Success || string.IsNullOrWhiteSpace(payment.PaymentUrl))
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new ApiResponse<object> { Success = false, Message = payment.Error ?? "Unable to create Paystack payment request." });
        }

        task.PaymentReference = payment.TransactionReference ?? task.PaymentReference;
        _context.PaymentAttempts.Add(new PaymentAttempt
        {
            TaskId = task.Id,
            Provider = "Paystack",
            Reference = task.PaymentReference!,
            Amount = task.Budget,
            Status = "Pending",
            PaymentUrl = payment.PaymentUrl,
            CreatedAt = DateTime.UtcNow
        });

        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = userId.Value,
            Action = "PaystackPaymentCreated",
            EntityType = "Task",
            EntityId = task.Id,
            NewValues = $"transactionReference={task.PaymentReference}; amount={task.Budget:F2}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });

        var paymentRecord = await _context.Payments.FirstOrDefaultAsync(p => p.TaskId == task.Id);
        if (paymentRecord == null)
        {
            _context.Payments.Add(new Payment
            {
                PaymentId = $"DFY-PAY-{task.TaskId}",
                TaskId = task.Id,
                Amount = task.Budget,
                PaymentMethod = "Online",
                Provider = "Paystack",
                PaymentUrl = payment.PaymentUrl,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow
            });
        }
        else
        {
            paymentRecord.Amount = task.Budget;
            paymentRecord.PaymentMethod = "Online";
            paymentRecord.Provider = "Paystack";
            paymentRecord.PaymentUrl = payment.PaymentUrl;
            paymentRecord.Status = "Pending";
        }
        await _context.SaveChangesAsync();

        var taskDto = new TaskDto
        {
            Id = task.Id,
            TaskId = task.TaskId,
            TaskName = task.TaskName,
            UserName = $"{user.FirstName} {user.LastName}",
            UserContact = user.PhoneNumber ?? user.Email,
            CreatedByUserId = task.CreatedByUserId,
            TaskDescription = task.TaskDescription,
            Category = task.Category,
            Area = task.Area,
            DateNeeded = task.DateNeeded,
            Budget = task.Budget,
            PayoutAmount = task.PayoutAmount,
            Notes = task.Notes,
            PaymentStatus = task.PaymentStatus,
            TaskStatus = task.TaskStatus,
            Priority = task.Priority,
            CreatedAt = task.CreatedAt
        };

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { task = taskDto, paymentUrl = payment.PaymentUrl },
            Message = "Task created successfully. Complete payment to activate."
        });
    }

    [HttpGet("available")]
    [AllowAnonymous]
    public async Task<ActionResult<PaginatedResponse<TaskDto>>> GetAvailableTasks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] string? category = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _context.Tasks
            .Include(t => t.CreatedByUser)
            .Where(t => t.PaymentStatus == "EscrowHeld" && t.TaskStatus == "Posted");

        if (!string.IsNullOrEmpty(search)) query = query.Where(t => t.TaskDescription.Contains(search) || t.Area.Contains(search));
        if (!string.IsNullOrEmpty(category)) query = query.Where(t => t.Category == category);

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var currentUserId = GetCurrentUserId();
        User? currentUser = null;
        if (currentUserId.HasValue)
            currentUser = await _context.Users.FindAsync(currentUserId.Value);

        var tasks = await query.OrderByDescending(t => t.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(t => new TaskDto
            {
                Id = t.Id, TaskId = t.TaskId, TaskName = t.TaskName,
                UserName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}", UserContact = string.Empty,
                CreatedByUserId = t.CreatedByUserId, TaskDescription = t.TaskDescription, Category = t.Category,
                Area = t.Area, DateNeeded = t.DateNeeded, Budget = t.Budget, PayoutAmount = t.PayoutAmount, Notes = t.Notes,
                PaymentStatus = t.PaymentStatus, TaskStatus = t.TaskStatus, HelperName = t.HelperName,
                HelperContact = t.HelperContact, Priority = t.Priority, CreatedAt = t.CreatedAt, CompletedAt = t.CompletedAt, EscrowHoldUntil = t.EscrowHoldUntil, PayoutStatus = t.PayoutStatus
            }).ToListAsync();

        if (currentUser != null)
        {
            var canAccept = _userPolicyService.CanAcceptTasks(currentUser);
            foreach (var availableTask in tasks)
                availableTask.CanAccept = canAccept && availableTask.CreatedByUserId != currentUser.Id;
        }

        return Ok(new PaginatedResponse<TaskDto> { Success = true, Count = totalCount, Page = page, PageSize = pageSize, TotalPages = totalPages, Tasks = tasks });
    }

    [HttpGet("my-posted")]
    public async Task<ActionResult<ApiResponse<PaginatedResponse<TaskDto>>>> GetMyPostedTasks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? taskId = null)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var query = _context.Tasks
            .Where(t => t.CreatedByUserId == userId);
        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        if (!string.IsNullOrWhiteSpace(taskId))
        {
            var target = await query
                .Where(t => t.TaskId == taskId)
                .Select(t => new { t.Id, t.CreatedAt })
                .FirstOrDefaultAsync();
            if (target != null)
            {
                var tasksBeforeTarget = await query.CountAsync(t =>
                    t.CreatedAt > target.CreatedAt ||
                    (t.CreatedAt == target.CreatedAt && t.Id > target.Id));
                page = tasksBeforeTarget / pageSize + 1;
            }
        }
        page = Math.Min(page, Math.Max(totalPages, 1));

        var tasks = await query.Include(t => t.CreatedByUser)
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TaskDto
            {
                Id = t.Id, TaskId = t.TaskId, TaskName = t.TaskName, UserName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                UserContact = t.CreatedByUser.PhoneNumber ?? t.CreatedByUser.Email, CreatedByUserId = t.CreatedByUserId,
                TaskDescription = t.TaskDescription, Category = t.Category, Area = t.Area, DateNeeded = t.DateNeeded,
                Budget = t.Budget, PayoutAmount = t.PayoutAmount, Notes = t.Notes, PaymentStatus = t.PaymentStatus, TaskStatus = t.TaskStatus,
                HelperName = t.HelperName, HelperContact = t.HelperContact, Priority = t.Priority,
                CreatedAt = t.CreatedAt, CompletedAt = t.CompletedAt, EscrowHoldUntil = t.EscrowHoldUntil, PayoutStatus = t.PayoutStatus
            }).ToListAsync();

        var response = new PaginatedResponse<TaskDto> { Success = true, Count = totalCount, Page = page, PageSize = pageSize, TotalPages = totalPages, Tasks = tasks };
        return Ok(new ApiResponse<PaginatedResponse<TaskDto>> { Success = true, Data = response });
    }

    [HttpGet("pending-payment")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetPendingPaymentTasks()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var tasks = await _context.Tasks.Where(t => t.CreatedByUserId == userId && t.TaskStatus == "PendingPayment").OrderByDescending(t => t.CreatedAt)
            .Select(t => new { taskId = t.TaskId, description = t.TaskDescription, budget = t.Budget, createdAt = t.CreatedAt, category = t.Category, area = t.Area })
            .Cast<object>().ToListAsync();
        return Ok(new ApiResponse<List<object>> { Success = true, Data = tasks, Message = "Pending payment tasks retrieved successfully" });
    }

    [HttpPost("{taskId}/claim")]
    public async Task<ActionResult<ApiResponse<bool>>> ClaimTask(string taskId, [FromBody] ClaimTaskRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
            return Unauthorized();

        if (!_userPolicyService.IsProfileComplete(user))
            return Ok(new ApiResponse<bool> { Success = false, Message = "Please complete your profile before claiming tasks" });

        if (!_userPolicyService.CanAcceptTasks(user))
            return StatusCode(StatusCodes.Status403Forbidden,
                new ApiResponse<bool> { Success = false, Message = "Only Runners and Both accounts can accept tasks." });

        if (!request.TermsAccepted)
            return BadRequest(new ApiResponse<bool> { Success = false, Message = "You must accept the runner terms before accepting a task." });

        if (string.IsNullOrWhiteSpace(request.HelperName) || string.IsNullOrWhiteSpace(request.HelperContact))
            return BadRequest(new ApiResponse<bool> { Success = false, Error = "RUNNER_DETAILS_REQUIRED", Message = "Runner name and contact details are required." });

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Error = "TASK_NOT_FOUND", Message = "Task could not be found." });

        if (task.CreatedByUserId == userId)
            return Conflict(new ApiResponse<bool> { Success = false, Error = "CANNOT_CLAIM_OWN_TASK", Message = "You cannot accept a task that you created." });

        if (task.AcceptedByUserId.HasValue || task.TaskStatus == "Claimed")
            return Conflict(new ApiResponse<bool> { Success = false, Error = "TASK_ALREADY_CLAIMED", Message = "This task has already been accepted by another runner." });

        if (task.TaskStatus != "Posted")
            return Conflict(new ApiResponse<bool> { Success = false, Error = "TASK_NOT_POSTED", Message = "This task is no longer available for acceptance." });

        if (task.PaymentStatus != "EscrowHeld")
            return Conflict(new ApiResponse<bool> { Success = false, Error = "PAYMENT_NOT_SECURED", Message = "This task cannot be accepted until the creator's payment is secured." });

        var hasVerifiedPayoutAccount = await _context.BankAccounts.AnyAsync(b =>
            b.UserId == userId.Value &&
            b.IsActive &&
            b.IsVerified &&
            b.ProviderReference != null &&
            b.ProviderReference != "");
        if (!hasVerifiedPayoutAccount)
            return Conflict(new ApiResponse<bool>
            {
                Success = false,
                Error = "PAYOUT_BANK_ACCOUNT_REQUIRED",
                Message = "Add and verify a Paystack bank account in Payout Settings before accepting a task."
            });

        // Atomic claim: exactly one runner can transition the posted task to Claimed.
        var claimedAt = DateTime.UtcNow;
        var affected = await _context.Tasks
            .Where(t => t.Id == task.Id && t.TaskStatus == "Posted" && t.PaymentStatus == "EscrowHeld" && t.AcceptedByUserId == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.AcceptedByUserId, userId.Value)
                .SetProperty(t => t.HelperName, request.HelperName.Trim())
                .SetProperty(t => t.HelperContact, request.HelperContact.Trim())
                .SetProperty(t => t.TaskStatus, "Claimed")
                .SetProperty(t => t.UpdatedAt, claimedAt));

        if (affected != 1)
            return Ok(new ApiResponse<bool> { Success = false, Message = "Task was just accepted by another runner." });

        await _notificationService.NotifyTaskClaimedAsync(task.CreatedByUserId, task.TaskDescription, request.HelperName.Trim());
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Task accepted successfully." });
    }

    [HttpPut("{taskId}/payment-status")]
    public IActionResult UpdatePaymentStatus(string taskId) => StatusCode(410, new ApiResponse<bool>
    {
        Success = false, Data = false, Message = "This endpoint has been retired. Payment status is updated by the payment provider notification."
    });

    [HttpGet("filters")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<object>>> GetFilterOptions()
    {
        var categories = await _context.Categories.Select(c => c.Name).ToListAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = new { categories, statuses = new[] { "Posted", "Claimed", "AwaitingConfirmation", "PayoutPending", "RunnerPaid", "Disputed" } } });
    }

    [HttpGet("{taskId}/payment-url")]
    public async Task<ActionResult<ApiResponse<object>>> GetPaymentUrl(string taskId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var task = await _context.Tasks.Include(t => t.CreatedByUser).FirstOrDefaultAsync(t => t.TaskId == taskId && t.CreatedByUserId == userId);
        if (task == null || task.TaskStatus != "PendingPayment")
            return Ok(new ApiResponse<object> { Success = false, Message = "Task not found or payment not pending" });

        // Every checkout attempt must receive a new Paystack reference. Reusing a
        // previous reference causes Paystack to reject the initialization.
        task.PaymentReference = GeneratePaymentReference(task.TaskId);
        var payment = await _paymentProvider.CreatePaymentAsync(task, task.CreatedByUser, HttpContext.RequestAborted);
        if (!payment.Success || string.IsNullOrWhiteSpace(payment.PaymentUrl))
            return StatusCode(StatusCodes.Status502BadGateway, new ApiResponse<object> { Success = false, Message = payment.Error ?? "Unable to create Paystack payment request." });

        task.PaymentReference = payment.TransactionReference ?? task.PaymentReference;
        _context.PaymentAttempts.Add(new PaymentAttempt
        {
            TaskId = task.Id,
            Provider = "Paystack",
            Reference = task.PaymentReference!,
            Amount = task.Budget,
            Status = "Pending",
            PaymentUrl = payment.PaymentUrl,
            CreatedAt = DateTime.UtcNow
        });

        _context.AuditLogs.Add(new Models.AuditLog
        {
            UserId = userId.Value,
            Action = "PaystackPaymentCreated",
            EntityType = "Task",
            EntityId = task.Id,
            NewValues = $"transactionReference={task.PaymentReference}; amount={task.Budget:F2}",
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Request.Headers.UserAgent.ToString()
        });

        var paymentRecord = await _context.Payments.FirstOrDefaultAsync(p => p.TaskId == task.Id);
        if (paymentRecord == null)
        {
            _context.Payments.Add(new Payment
            {
                PaymentId = $"DFY-PAY-{task.TaskId}",
                TaskId = task.Id,
                Amount = task.Budget,
                PaymentMethod = "Online",
                Provider = "Paystack",
                PaymentUrl = payment.PaymentUrl,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow
            });
        }
        else
        {
            paymentRecord.Amount = task.Budget;
            paymentRecord.PaymentMethod = "Online";
            paymentRecord.Provider = "Paystack";
            paymentRecord.PaymentUrl = payment.PaymentUrl;
            paymentRecord.Status = "Pending";
        }
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = new { paymentUrl = payment.PaymentUrl }, Message = "Payment URL generated successfully" });
    }

    [HttpGet("{taskId}")]
    public async Task<ActionResult<ApiResponse<object>>> GetTask(string taskId)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId == null) return Unauthorized();

        var task = await _context.Tasks.Include(t => t.CreatedByUser).Include(t => t.AcceptedByUser).FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null) return NotFound(new ApiResponse<object> { Success = false, Message = "Task not found" });

        // Detailed task views may contain participant contact details. Only the creator,
        // assigned runner, or an administrator may access them.
        var isAdmin = User.IsInRole("Admin");
        var isParticipant = task.CreatedByUserId == currentUserId ||
                            task.AcceptedByUserId == currentUserId;
        if (!isAdmin && !isParticipant)
            return Forbid();

        var progressUpdates = await _context.TaskProgressUpdates.Include(p => p.User).Where(p => p.TaskId == task.Id).OrderByDescending(p => p.CreatedAt)
            .Select(p => new { id = p.Id, message = p.Message, timestamp = p.CreatedAt, userId = p.UserId, userName = $"{p.User.FirstName} {p.User.LastName}" }).ToListAsync();

        var taskDto = new
        {
            id = task.Id, taskId = task.TaskId, title = task.TaskDescription, description = task.TaskDescription,
            category = task.Category, location = task.Area, budget = task.Budget, status = task.TaskStatus.ToLower(),
            paymentStatus = task.PaymentStatus, escrowStatus = task.EscrowStatus, priority = task.Priority.ToLower(),
            createdAt = task.CreatedAt, dueDate = task.DateNeeded,
            creatorName = $"{task.CreatedByUser.FirstName} {task.CreatedByUser.LastName}",
            creatorContact = task.CreatedByUser.PhoneNumber ?? task.CreatedByUser.Email,
            runnerName = task.AcceptedByUser != null ? $"{task.AcceptedByUser.FirstName} {task.AcceptedByUser.LastName}" : null,
            runnerContact = task.AcceptedByUser?.PhoneNumber ?? task.AcceptedByUser?.Email,
            runnerId = task.AcceptedByUserId, createdByUserId = task.CreatedByUserId, completedAt = task.CompletedAt,
            notes = task.Notes, progressUpdates = progressUpdates,
            canEdit = task.CreatedByUserId == currentUserId && task.TaskStatus == "PendingPayment",
            canComplete = task.AcceptedByUserId == currentUserId && task.TaskStatus == "Claimed",
            canCancel = task.CreatedByUserId == currentUserId && task.TaskStatus == "PendingPayment",
            canConfirm = task.CreatedByUserId == currentUserId && task.TaskStatus == "AwaitingConfirmation",
            canMessage = task.CreatedByUserId == currentUserId || task.AcceptedByUserId == currentUserId,
            canDispute = task.CreatedByUserId == currentUserId || task.AcceptedByUserId == currentUserId
        };

        return Ok(new ApiResponse<object> { Success = true, Data = taskDto, Message = "Task retrieved successfully" });
    }

    [HttpPost("{taskId}/complete")]
    public async Task<ActionResult<ApiResponse<bool>>> CompleteTask(string taskId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || task.AcceptedByUserId != userId || task.TaskStatus != "Claimed")
            return Ok(new ApiResponse<bool> { Success = false, Message = "Cannot complete task" });

        task.TaskStatus = "AwaitingConfirmation";
        task.PaymentStatus = "EscrowHeld";
        task.EscrowStatus = "held";
        task.CompletedAt = DateTime.UtcNow;
        // The creator has 24 hours to confirm completion or raise a dispute.
        // If there is no action, the background escrow service auto-releases payment.
        task.EscrowHoldUntil = DateTime.UtcNow.AddHours(24);
        task.UpdatedAt = DateTime.UtcNow;

        var runner = await _context.Users.FindAsync(userId.Value);
        _context.TaskMessages.Add(new TaskMessage
        {
            TaskId = task.Id,
            SenderId = userId.Value,
            Content = "[SYSTEM] The runner marked this task as completed. Please review the work and confirm the task.",
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync();

        await _notificationService.NotifyTaskCompletedAsync(task.CreatedByUserId, task.TaskDescription, $"{runner?.FirstName} {runner?.LastName}");
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Task completed! You have 24 hours to confirm completion or raise a dispute. If you take no action, payment will be released automatically." });
    }

    [HttpPost("{taskId}/confirm")]
    public async Task<ActionResult<ApiResponse<bool>>> ConfirmTask(string taskId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null || task.CreatedByUserId != userId || (task.TaskStatus != "AwaitingConfirmation" && task.TaskStatus != "RunnerPaid"))
            return Ok(new ApiResponse<bool> { Success = false, Message = "Cannot confirm task" });
        if (task.TaskStatus == "RunnerPaid")
            return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Payment already released" });

        var released = await _escrowService.ReleaseEscrowAsync(task.Id, force: true);
        if (!released) return Ok(new ApiResponse<bool> { Success = false, Message = "Failed to release payment" });

        _context.TaskMessages.Add(new TaskMessage
        {
            TaskId = task.Id,
            SenderId = userId.Value,
            Content = "[SYSTEM] This task has been confirmed complete. The conversation is now closed.",
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        await _notificationService.NotifyPaymentReleasedAsync(task.AcceptedByUserId!.Value, task.TaskDescription, task.PayoutAmount);
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Task confirmed and payout initiated!" });
    }

    [HttpGet("my-active")]
    public async Task<ActionResult<ApiResponse<object>>> GetMyActiveTasks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? taskId = null)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var query = _context.Tasks
            .Where(t => t.AcceptedByUserId == userId && t.TaskStatus == "Claimed");
        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            var target = await query
                .Where(t => t.TaskId == taskId)
                .Select(t => new { t.Id, t.UpdatedAt })
                .FirstOrDefaultAsync();
            if (target != null)
            {
                var tasksBeforeTarget = await query.CountAsync(t =>
                    t.UpdatedAt > target.UpdatedAt ||
                    (t.UpdatedAt == target.UpdatedAt && t.Id > target.Id));
                page = tasksBeforeTarget / pageSize + 1;
            }
        }
        page = Math.Min(page, Math.Max(totalPages, 1));
        var tasks = await query
            .OrderByDescending(t => t.UpdatedAt)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                id = t.Id,
                taskId = t.TaskId,
                title = t.TaskDescription,
                description = t.TaskDescription,
                category = t.Category,
                location = t.Area,
                budget = t.Budget,
                payoutAmount = t.PayoutAmount,
                status = t.TaskStatus.ToLower(),
                createdAt = t.CreatedAt,
                dueDate = t.DateNeeded,
                creatorName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}",
                creatorContact = t.CreatedByUser.PhoneNumber ?? t.CreatedByUser.Email
            })
            .ToListAsync();
        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { tasks, count = totalCount, page, pageSize, totalPages },
            Message = "Active tasks retrieved successfully"
        });
    }

    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<TaskDto>>> GetUserTasks([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var query = _context.Tasks.Include(t => t.CreatedByUser).Where(t => t.CreatedByUserId == userId);
        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        var tasks = await query.OrderByDescending(t => t.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).Select(t => new TaskDto
        {
            Id = t.Id, TaskId = t.TaskId, TaskName = t.TaskName, UserName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}", UserContact = t.CreatedByUser.PhoneNumber ?? t.CreatedByUser.Email,
            CreatedByUserId = t.CreatedByUserId, TaskDescription = t.TaskDescription, Category = t.Category, Area = t.Area, DateNeeded = t.DateNeeded,
            Budget = t.Budget, PayoutAmount = t.PayoutAmount, Notes = t.Notes, PaymentStatus = t.PaymentStatus, TaskStatus = t.TaskStatus, HelperName = t.HelperName,
            HelperContact = t.HelperContact, Priority = t.Priority, CreatedAt = t.CreatedAt, CompletedAt = t.CompletedAt
        }).ToListAsync();
        return Ok(new PaginatedResponse<TaskDto> { Success = true, Count = totalCount, Page = page, PageSize = pageSize, TotalPages = totalPages, Tasks = tasks });
    }

    [HttpPut("{taskId}")]
    public async Task<ActionResult<ApiResponse<object>>> UpdateTask(string taskId, [FromBody] UpdateTaskRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId && t.CreatedByUserId == userId);
        if (task == null) return Ok(new ApiResponse<object> { Success = false, Message = "Task not found" });
        if (task.TaskStatus != "PendingPayment") return Ok(new ApiResponse<object> { Success = false, Message = "Only pending payment tasks can be edited" });

        if (!string.IsNullOrWhiteSpace(request.TaskDescription)) task.TaskDescription = request.TaskDescription.Trim();
        if (!string.IsNullOrWhiteSpace(request.Category)) task.Category = request.Category.Trim();
        if (!string.IsNullOrWhiteSpace(request.Area)) task.Area = request.Area.Trim();
        if (!string.IsNullOrWhiteSpace(request.Priority)) task.Priority = request.Priority.Trim();
        if (request.Notes != null) task.Notes = request.Notes.Trim();
        if (request.DateNeeded.HasValue)
        {
            var dateNeeded = DateTime.SpecifyKind(request.DateNeeded.Value, DateTimeKind.Utc);
            if (dateNeeded <= DateTime.UtcNow.AddMinutes(30) || dateNeeded > DateTime.UtcNow.AddDays(365))
                return Ok(new ApiResponse<object> { Success = false, Message = "Date needed must be more than 30 minutes and no more than 365 days in the future." });
            task.DateNeeded = dateNeeded;
        }
        if (request.Budget.HasValue && request.Budget.Value >= 50)
        {
            task.Budget = request.Budget.Value;
            var commission = _escrowService.CalculateCommission(task.Budget);
            task.CommissionAmount = commission;
            task.PayoutAmount = task.Budget - commission;
        }
        task.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<object> { Success = true, Data = new { taskId = task.TaskId }, Message = "Task updated successfully" });
    }

    [HttpDelete("{taskId}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteTask(string taskId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId && t.CreatedByUserId == userId);
        if (task == null) return Ok(new ApiResponse<bool> { Success = false, Message = "Task not found" });
        if (task.TaskStatus != "PendingPayment") return Ok(new ApiResponse<bool> { Success = false, Message = "Cannot delete task after payment" });
        task.IsDeleted = true; task.DeletedAt = DateTime.UtcNow; task.TaskStatus = "Cancelled"; task.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Task deleted successfully" });
    }

    [HttpPost("payment-success")]
    [AllowAnonymous]
    public IActionResult HandlePaymentSuccess() => StatusCode(StatusCodes.Status410Gone,
        new ApiResponse<bool> { Success = false, Data = false, Message = "Payment status is updated only by the Ozow payment notification." });

    [HttpGet("dashboard/stats")]
    public async Task<ActionResult<ApiResponse<object>>> GetTaskDashboardStats()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        var now = DateTime.UtcNow;
        var currentMonth = now.Month;
        var currentYear = now.Year;

        var postedTasks = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId);
        var pendingPayment = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId && t.TaskStatus == "PendingPayment");
        var creatorActiveTasks = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId && (t.TaskStatus == "Claimed" || t.TaskStatus == "PayoutPending"));
        var awaitingConfirmation = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId && t.TaskStatus == "AwaitingConfirmation");
        var creatorCompletedTasks = await _context.Tasks.CountAsync(t => t.CreatedByUserId == userId && t.TaskStatus == "RunnerPaid");

        var totalSpent = await _context.Tasks.Where(t => t.CreatedByUserId == userId && t.TaskStatus == "RunnerPaid").SumAsync(t => t.Budget);
        var thisMonthSpending = await _context.Tasks.Where(t => t.CreatedByUserId == userId && t.TaskStatus == "RunnerPaid" && t.UpdatedAt.Month == currentMonth && t.UpdatedAt.Year == currentYear).SumAsync(t => t.Budget);
        var averageTaskCost = creatorCompletedTasks > 0 ? totalSpent / creatorCompletedTasks : 0;

        var availableTasks = await _context.Tasks.CountAsync(t => t.TaskStatus == "Posted" && t.PaymentStatus == "EscrowHeld");
        var runnerActiveTasks = await _context.Tasks.CountAsync(t => t.AcceptedByUserId == userId && t.TaskStatus == "Claimed");
        var runnerCompletedTasks = await _context.Tasks.CountAsync(t => t.AcceptedByUserId == userId && (t.TaskStatus == "AwaitingConfirmation" || t.TaskStatus == "PayoutPending" || t.TaskStatus == "RunnerPaid"));

        var completedPayouts = await _context.Payouts.Where(p => p.RunnerId == userId && p.Status == "Completed").ToListAsync();
        var totalEarnings = completedPayouts.Sum(p => p.Amount);
        var pendingPayouts = await _context.Payouts.Where(p => p.RunnerId == userId && (p.Status == "Pending" || p.Status == "Processing" || p.Status == "AwaitingFunds" || p.Status == "AwaitingBankDetails")).SumAsync(p => p.Amount);
        var thisMonthEarnings = completedPayouts.Where(p => p.CompletedAt.HasValue && p.CompletedAt.Value.Month == currentMonth && p.CompletedAt.Value.Year == currentYear).Sum(p => p.Amount);
        var totalAcceptedTasks = await _context.Tasks.CountAsync(t => t.AcceptedByUserId == userId);
        var completionRate = totalAcceptedTasks > 0 ? (runnerCompletedTasks * 100) / totalAcceptedTasks : 0;
        var averageEarning = completedPayouts.Count > 0 ? totalEarnings / completedPayouts.Count : 0;

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new
            {
                postedTasks, pendingPayment, activeTasks = creatorActiveTasks, awaitingConfirmation,
                completedTasks = creatorCompletedTasks, totalSpent, thisMonthSpending, averageTaskCost,
                availableTasks, myActiveTasks = runnerActiveTasks, runnerCompletedTasks,
                totalEarnings, pendingPayouts, thisMonthEarnings, completionRate, averageEarning,
                myRating = user?.Rating ?? 0
            }
        });
    }

    [HttpGet("dashboard/activity")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetDashboardActivity([FromQuery] int limit = 5)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        limit = Math.Clamp(limit, 1, 20);
        var activities = await _context.Tasks.Include(t => t.CreatedByUser).Where(t => t.CreatedByUserId == userId || t.AcceptedByUserId == userId).OrderByDescending(t => t.UpdatedAt).Take(limit)
            .Select(t => new { id = t.Id, taskId = t.TaskId, description = t.TaskDescription, status = t.TaskStatus, updatedAt = t.UpdatedAt, type = t.CreatedByUserId == userId ? "created" : "accepted" }).Cast<object>().ToListAsync();
        return Ok(new ApiResponse<List<object>> { Success = true, Data = activities });
    }

    [HttpGet("payment-history")]
    public async Task<ActionResult<ApiResponse<List<object>>>> GetPaymentHistory()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var payments = await _context.Tasks.Where(t => t.CreatedByUserId == userId && (t.PaymentStatus == "EscrowHeld" || t.PaymentStatus == "EscrowReleased"))
            .OrderByDescending(t => t.UpdatedAt).Select(t => new { taskId = t.TaskId, amount = t.Budget, status = t.PaymentStatus, date = t.UpdatedAt, description = t.TaskDescription }).Cast<object>().ToListAsync();
        return Ok(new ApiResponse<List<object>> { Success = true, Data = payments, Message = "Payment history retrieved successfully" });
    }

    [HttpGet("my-completed")]
    public async Task<ActionResult<ApiResponse<object>>> GetMyCompletedTasks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? taskId = null)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var query = _context.Tasks
            .Where(t => t.AcceptedByUserId == userId &&
                        (t.TaskStatus == "AwaitingConfirmation" ||
                         t.TaskStatus == "Completed" ||
                         t.TaskStatus == "PayoutPending" ||
                         t.TaskStatus == "RunnerPaid" ||
                         t.TaskStatus == "PayoutFailed"));

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            var target = await query
                .Where(t => t.TaskId == taskId)
                .Select(t => new { t.Id, t.CompletedAt })
                .FirstOrDefaultAsync();

            if (target != null)
            {
                var tasksBeforeTarget = await query.CountAsync(t =>
                    (target.CompletedAt == null && t.CompletedAt != null) ||
                    (t.CompletedAt.HasValue == target.CompletedAt.HasValue &&
                     (t.CompletedAt > target.CompletedAt ||
                      (t.CompletedAt == target.CompletedAt && t.Id > target.Id))));
                page = tasksBeforeTarget / pageSize + 1;
            }
        }

        var tasks = await query
            .OrderBy(t => t.CompletedAt == null)
            .ThenByDescending(t => t.CompletedAt)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                id = t.Id,
                taskId = t.TaskId,
                title = t.TaskDescription,
                category = t.Category,
                location = t.Area,
                budget = t.Budget,
                payoutAmount = t.PayoutAmount,
                status = t.TaskStatus.ToLower(),
                paymentStatus = t.PaymentStatus,
                payoutStatus = t.PayoutStatus,
                payoutReference = t.PayoutReference,
                payoutInitiatedAt = t.PayoutInitiatedAt,
                payoutCompletedAt = t.PayoutCompletedAt,
                completedAt = t.CompletedAt,
                creatorName = $"{t.CreatedByUser.FirstName} {t.CreatedByUser.LastName}"
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Data = new { tasks, count = totalCount, page, pageSize, totalPages }
        });
    }

    [HttpPost("{taskId}/cancel")]
    public async Task<ActionResult<ApiResponse<bool>>> CancelTask(string taskId, [FromBody] CancelTaskRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null) return Ok(new ApiResponse<bool> { Success = false, Message = "Task not found" });
        if (task.CreatedByUserId != userId && task.AcceptedByUserId != userId) return Ok(new ApiResponse<bool> { Success = false, Message = "Not authorized" });
        if (task.TaskStatus != "PendingPayment")
            return Ok(new ApiResponse<bool> { Success = false, Message = "Paid tasks cannot be cancelled through this endpoint. Raise a dispute for admin review." });

        task.TaskStatus = "Cancelled";
        task.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Task cancelled" });
    }

    [HttpPost("cleanup")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<bool>>> CleanupExpiredTasks()
    {
        var expiry = DateTime.UtcNow.AddHours(-24);
        var expired = await _context.Tasks.Where(t => t.TaskStatus == "PendingPayment" && t.CreatedAt < expiry).ToListAsync();
        foreach (var task in expired) { task.IsDeleted = true; task.DeletedAt = DateTime.UtcNow; task.TaskStatus = "Cancelled"; }
        await _context.SaveChangesAsync();
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = $"{expired.Count} expired tasks cleaned up" });
    }

    [HttpPost("payment/initiate")]
    public IActionResult InitiatePayment([FromBody] InitiatePaymentRequest request) => StatusCode(StatusCodes.Status410Gone,
        new ApiResponse<object> { Success = false, Message = "This endpoint has been retired. Use the task payment URL endpoint." });

    private int? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private static string GeneratePaymentReference(string taskId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return $"dfy-pay-{taskId}-{suffix}";
    }

    private string GenerateTaskId()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var random = Random.Shared.Next(1000, 9999);
        return $"DFY-{timestamp}-{random}";
    }
}
