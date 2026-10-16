// Full DFY staging scenario validation runs alongside the API unit scenarios.
using System.Security.Claims;
using System.Text.Json;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaskEntity = DoForYou.API.Models.Task;
using TaskResult = System.Threading.Tasks.Task;

namespace DoForYou.Tests;

public class ScenarioTests
{
    [Fact]
    public async System.Threading.Tasks.Task PosterCanCreateTask_WhenProfileIsComplete()
    {
        using var context = CreateContext(nameof(PosterCanCreateTask_WhenProfileIsComplete));
        var poster = CreateUser(1, "creator", true, "poster@example.com");
        context.Users.Add(poster);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, poster.Id);
        var request = new CreateTaskRequest
        {
            TaskName = "Fix the leaking tap",
            TaskDescription = "Fix the leaking tap",
            Category = "Home",
            Area = "Johannesburg",
            DateNeeded = DateTime.UtcNow.AddDays(2),
            Budget = 500m,
            Notes = "Urgent fix",
            Priority = "High",
            TermsAccepted = true,
        };

        var result = await controller.CreateTask(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);

        Assert.True(response.Success, response.Message);
        Assert.Equal("Task created successfully. Complete payment to activate.", response.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task BothAccountCanPostWithoutRunnerIdentityOrPayoutSetup()
    {
        using var context = CreateContext(nameof(BothAccountCanPostWithoutRunnerIdentityOrPayoutSetup));
        var user = CreateUser(1, "both", false, "both-without-kyc@test.com");
        user.IdNumber = null;
        user.DateOfBirth = null;
        user.Address = null;
        user.IdentityVerificationStatus = "not_started";
        user.AuthenticatorTwoFactorEnabled = false;
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, user.Id);
        var result = await controller.CreateTask(new CreateTaskRequest
        {
            TaskName = "Book a local gardener",
            TaskDescription = "Find someone to mow the lawn and clear the garden this weekend.",
            Category = "Home",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(2),
            Budget = 200m,
            Priority = "Standard",
            TermsAccepted = true
        });

        var response = Assert.IsType<ApiResponse<object>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(response.Success, response.Message);
        Assert.Single(await context.Tasks.ToListAsync());
        Assert.Null(user.IdNumber);
        Assert.Null(user.Address);
        Assert.Equal("not_started", user.IdentityVerificationStatus);
        Assert.False(user.AuthenticatorTwoFactorEnabled);
    }

    [Fact]
    public async System.Threading.Tasks.Task UnverifiedBothAccountCannotPostAndCreatesNoTask()
    {
        using var context = CreateContext(nameof(UnverifiedBothAccountCannotPostAndCreatesNoTask));
        var user = CreateUser(1, "both", false, "unverified-both@test.com");
        user.EmailVerified = false;
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, user.Id);
        var result = await controller.CreateTask(new CreateTaskRequest
        {
            TaskName = "Book a local gardener",
            TaskDescription = "Find someone to mow the lawn and clear the garden this weekend.",
            Category = "Home",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(2),
            Budget = 200m,
            Priority = "Standard",
            TermsAccepted = true
        });

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Empty(await context.Tasks.ToListAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task RunnerOnlyAccountCannotPostTask()
    {
        using var context = CreateContext(nameof(RunnerOnlyAccountCannotPostTask));
        var user = CreateUser(1, "runner", true, "runner-only@test.com");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, user.Id);
        var result = await controller.CreateTask(new CreateTaskRequest
        {
            TaskName = "Book a local gardener",
            TaskDescription = "Find someone to mow the lawn and clear the garden this weekend.",
            Category = "Home",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(2),
            Budget = 200m,
            Priority = "Standard",
            TermsAccepted = true
        });

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Empty(await context.Tasks.ToListAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task TaskBudgetAboveMaximumIsRejectedBeforeAnyTaskIsCreated()
    {
        using var context = CreateContext(nameof(TaskBudgetAboveMaximumIsRejectedBeforeAnyTaskIsCreated));
        var user = CreateUser(1, "creator", true, "budget-limit@test.com");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, user.Id);
        var result = await controller.CreateTask(new CreateTaskRequest
        {
            TaskName = "Book a local gardener",
            TaskDescription = "Find someone to mow the lawn and clear the garden this weekend.",
            Category = "Home",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(2),
            Budget = 100000.01m,
            Priority = "Standard",
            TermsAccepted = true
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<object>>(badRequest.Value);
        Assert.False(response.Success);
        Assert.Equal("INVALID_TASK_BUDGET", response.Error);
        Assert.Empty(await context.Tasks.ToListAsync());
        Assert.Empty(await context.PaymentAttempts.ToListAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task TaskDateInsideThirtyMinuteBoundaryIsRejectedWithoutSideEffects()
    {
        using var context = CreateContext(nameof(TaskDateInsideThirtyMinuteBoundaryIsRejectedWithoutSideEffects));
        var user = CreateUser(1, "creator", true, "task-date-boundary@test.com");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, user.Id);
        var result = await controller.CreateTask(new CreateTaskRequest
        {
            TaskName = "Book a local gardener",
            TaskDescription = "Find someone to mow the lawn and clear the garden this weekend.",
            Category = "Home",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddMinutes(5),
            Budget = 200m,
            Priority = "Standard",
            TermsAccepted = true
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<object>>(badRequest.Value);
        Assert.Equal("INVALID_TASK_DATE", response.Error);
        Assert.Empty(await context.Tasks.ToListAsync());
    }

    [Fact]
    public async System.Threading.Tasks.Task Notifications_LinksLegacyPayoutByMatchingTaskDescription()
    {
        using var context = CreateContext(nameof(Notifications_LinksLegacyPayoutByMatchingTaskDescription));
        var creator = CreateUser(1, "creator", true, "creator-notifications@test.com");
        var runner = CreateUser(2, "runner", true, "runner-notifications@test.com");
        var task = new TaskEntity
        {
            TaskId = "DFY-LEGACY-PAYOUT-1",
            TaskName = "Legacy payout",
            TaskDescription = "Legacy payout task",
            Category = "Other",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(1),
            Budget = 200m,
            CreatedByUserId = creator.Id,
            AcceptedByUserId = runner.Id,
            TaskStatus = "RunnerPaid",
            UpdatedAt = DateTime.UtcNow
        };
        context.Users.AddRange(creator, runner);
        context.Tasks.Add(task);
        context.Notifications.Add(new Notification
        {
            UserId = runner.Id,
            Type = "payout_pending",
            Title = "Payout Initiated",
            Message = "Your R170.00 runner payout is queued for: Legacy payout task",
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var controller = new NotificationsController(context);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, runner.Id.ToString())]));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await controller.GetNotifications();
        var response = Assert.IsType<ApiResponse<List<object>>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Data));

        Assert.Equal(task.Id, json.RootElement[0].GetProperty("relatedTaskId").GetInt32());
        Assert.Equal(task.TaskId, json.RootElement[0].GetProperty("relatedTaskStringId").GetString());
    }

    [Fact]
    public async System.Threading.Tasks.Task PaymentUrlRetry_CreatesUniquePaystackAttempts()
    {
        using var context = CreateContext(nameof(PaymentUrlRetry_CreatesUniquePaystackAttempts));
        var creator = CreateUser(1, "creator", true, "creator-payments@test.com");
        context.Users.Add(creator);

        var task = new TaskEntity
        {
            TaskId = "DFY-PAY-RETRY-1",
            TaskName = "Payment retry test",
            TaskDescription = "Testing unique Paystack payment attempts",
            Category = "Other",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(2),
            Budget = 500m,
            CommissionAmount = 75m,
            PayoutAmount = 425m,
            PaymentStatus = "Pending",
            TaskStatus = "PendingPayment",
            EscrowStatus = "pending",
            CreatedByUserId = creator.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, creator.Id);

        var first = await controller.GetPaymentUrl(task.TaskId);
        var second = await controller.GetPaymentUrl(task.TaskId);

        Assert.True(((ApiResponse<object>)((OkObjectResult)first.Result!).Value!).Success);
        Assert.True(((ApiResponse<object>)((OkObjectResult)second.Result!).Value!).Success);

        var attempts = await context.PaymentAttempts
            .Where(a => a.TaskId == task.Id)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, attempts.Count);
        Assert.NotEqual(attempts[0].Reference, attempts[1].Reference);
        Assert.All(attempts, attempt =>
        {
            Assert.Equal("Paystack", attempt.Provider);
            Assert.Equal(task.Budget, attempt.Amount);
            Assert.Equal("Pending", attempt.Status);
        });

        var refreshed = await context.Tasks.SingleAsync(t => t.Id == task.Id);
        Assert.Equal(attempts[1].Reference, refreshed.PaymentReference);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunnerCanClaimPostedTask()
    {
        using var context = CreateContext(nameof(RunnerCanClaimPostedTask));
        var poster = CreateUser(1, "creator", true, "poster@test.com");
        var runner = CreateUser(2, "runner", true, "runner@test.com");
        context.Users.AddRange(poster, runner);
        context.BankAccounts.Add(CreateVerifiedBankAccount(runner.Id));

        var task = new TaskEntity
        {
            TaskId = "DFY-1",
            TaskDescription = "Garden maintenance",
            Category = "Home",
            Area = "Cape Town",
            DateNeeded = DateTime.UtcNow.AddDays(3),
            Budget = 750m,
            CommissionAmount = 112.50m,
            PayoutAmount = 637.50m,
            PaymentStatus = "EscrowHeld",
            TaskStatus = "Posted",
            EscrowStatus = "held",
            CreatedByUserId = poster.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, runner.Id);
        var result = await controller.ClaimTask(task.TaskId, new ClaimTaskRequest { HelperName = "Runner Name", HelperContact = "0820000000", TermsAccepted = true });
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<bool>>(ok.Value);

        var savedTask = await context.Tasks.SingleAsync(t => t.Id == task.Id);
        Assert.True(response.Success);
        Assert.Equal("Claimed", savedTask.TaskStatus);
        Assert.Equal(runner.Id, savedTask.AcceptedByUserId);
    }

    [Fact]
    public async System.Threading.Tasks.Task VerifiedRunnerCanClaimTaskBeforePayoutAccountSetup()
    {
        using var context = CreateContext(nameof(VerifiedRunnerCanClaimTaskBeforePayoutAccountSetup));
        var poster = CreateUser(1, "creator", true, "poster@test.com");
        var runner = CreateUser(2, "runner", true, "runner@test.com");
        context.Users.AddRange(poster, runner);
        context.BankAccounts.Add(new BankAccount
        {
            UserId = runner.Id,
            BankName = "Legacy Test Bank",
            AccountNumber = "1234567890",
            AccountHolderName = "Test Runner",
            BranchCode = "123456",
            AccountType = "Savings",
            IsVerified = true,
            IsActive = true
        });

        var task = new TaskEntity
        {
            TaskId = "DFY-NO-BANK",
            TaskDescription = "Task requiring verified payout destination",
            Category = "Home",
            Area = "Cape Town",
            DateNeeded = DateTime.UtcNow.AddDays(3),
            Budget = 200m,
            PaymentStatus = "EscrowHeld",
            TaskStatus = "Posted",
            EscrowStatus = "held",
            CreatedByUserId = poster.Id
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, runner.Id);
        var result = await controller.ClaimTask(task.TaskId, new ClaimTaskRequest
        {
            HelperName = "Runner Name",
            HelperContact = "0820000000",
            TermsAccepted = true
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<bool>>(ok.Value);
        Assert.True(response.Success);
        Assert.Equal("Claimed", (await context.Tasks.SingleAsync(t => t.Id == task.Id)).TaskStatus);
    }

    [Fact]
    public async System.Threading.Tasks.Task MyCompletedTasks_ReturnsRequestedPageAndFindsLinkedTaskPage()
    {
        using var context = CreateContext(nameof(MyCompletedTasks_ReturnsRequestedPageAndFindsLinkedTaskPage));
        var creator = CreateUser(1, "creator", true, "creator@test.com");
        var runner = CreateUser(2, "runner", true, "runner@test.com");
        context.Users.AddRange(creator, runner);
        for (var index = 1; index <= 12; index++)
        {
            context.Tasks.Add(new TaskEntity
            {
                TaskId = $"DFY-COMPLETED-{index}",
                TaskName = $"Task {index}",
                TaskDescription = $"Completed task {index}",
                Category = "Other",
                Area = "Pretoria",
                DateNeeded = DateTime.UtcNow.AddDays(1),
                Budget = 200m,
                PayoutAmount = 170m,
                PaymentStatus = "EscrowReleased",
                TaskStatus = index == 11 ? "AwaitingConfirmation" : index == 12 ? "PayoutFailed" : "RunnerPaid",
                PayoutStatus = index == 11 ? "Pending" : index == 12 ? "Failed" : "Completed",
                EscrowStatus = "released",
                CreatedByUserId = creator.Id,
                AcceptedByUserId = runner.Id,
                CompletedAt = DateTime.UtcNow.AddDays(-index)
            });
        }
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, runner.Id);
        var secondPageResult = await controller.GetMyCompletedTasks(page: 2, pageSize: 5);
        var secondPageResponse = Assert.IsType<ApiResponse<object>>(
            Assert.IsType<OkObjectResult>(secondPageResult.Result).Value);
        var secondPage = JsonSerializer.SerializeToElement(secondPageResponse.Data);
        Assert.Equal(12, secondPage.GetProperty("count").GetInt32());
        Assert.Equal(2, secondPage.GetProperty("page").GetInt32());
        Assert.Equal(3, secondPage.GetProperty("totalPages").GetInt32());
        Assert.Equal(5, secondPage.GetProperty("tasks").GetArrayLength());
        Assert.Equal(12, secondPage.GetProperty("statusCounts").GetProperty("all").GetInt32());
        Assert.Equal(10, secondPage.GetProperty("statusCounts").GetProperty("paid").GetInt32());
        Assert.Equal(1, secondPage.GetProperty("statusCounts").GetProperty("awaitingCreator").GetInt32());

        var payoutIssueResult = await controller.GetMyCompletedTasks(
            page: 1,
            pageSize: 5,
            status: "payoutIssue");
        var payoutIssueResponse = Assert.IsType<ApiResponse<object>>(
            Assert.IsType<OkObjectResult>(payoutIssueResult.Result).Value);
        var payoutIssuePage = JsonSerializer.SerializeToElement(payoutIssueResponse.Data);
        Assert.Equal(1, payoutIssuePage.GetProperty("count").GetInt32());
        Assert.Equal("DFY-COMPLETED-12", payoutIssuePage.GetProperty("tasks")[0].GetProperty("taskId").GetString());
        Assert.Equal(10, payoutIssuePage.GetProperty("statusCounts").GetProperty("paid").GetInt32());

        var linkedTaskResult = await controller.GetMyCompletedTasks(
            page: 1,
            pageSize: 5,
            taskId: "DFY-COMPLETED-12");
        var linkedTaskResponse = Assert.IsType<ApiResponse<object>>(
            Assert.IsType<OkObjectResult>(linkedTaskResult.Result).Value);
        var linkedTaskPage = JsonSerializer.SerializeToElement(linkedTaskResponse.Data);
        Assert.Equal(3, linkedTaskPage.GetProperty("page").GetInt32());
        Assert.Contains(
            linkedTaskPage.GetProperty("tasks").EnumerateArray(),
            task => task.GetProperty("taskId").GetString() == "DFY-COMPLETED-12");
    }

    [Fact]
    public async System.Threading.Tasks.Task MyPostedAndActiveTasks_ReturnPagesAndFindLinkedPostedTaskPage()
    {
        using var context = CreateContext(nameof(MyPostedAndActiveTasks_ReturnPagesAndFindLinkedPostedTaskPage));
        var creator = CreateUser(1, "creator", true, "creator@test.com");
        var runner = CreateUser(2, "runner", true, "runner@test.com");
        context.Users.AddRange(creator, runner);
        for (var index = 1; index <= 12; index++)
        {
            context.Tasks.Add(new TaskEntity
            {
                TaskId = $"DFY-ACTIVE-{index}",
                TaskName = $"Task {index}",
                TaskDescription = $"Active task {index}",
                Category = "Other",
                Area = "Pretoria",
                DateNeeded = DateTime.UtcNow.AddDays(1),
                Budget = 200m,
                PayoutAmount = 170m,
                PaymentStatus = "EscrowHeld",
                TaskStatus = "Claimed",
                CreatedByUserId = creator.Id,
                AcceptedByUserId = runner.Id,
                CreatedAt = DateTime.UtcNow.AddDays(-index),
                UpdatedAt = DateTime.UtcNow.AddDays(-index)
            });
        }
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, creator.Id);
        var postedResult = await controller.GetMyPostedTasks(page: 2, pageSize: 5);
        var postedResponse = Assert.IsType<ApiResponse<PaginatedResponse<TaskDto>>>(
            Assert.IsType<OkObjectResult>(postedResult.Result).Value);
        Assert.Equal(12, postedResponse.Data!.Count);
        Assert.Equal(2, postedResponse.Data.Page);
        Assert.Equal(3, postedResponse.Data.TotalPages);
        Assert.Equal(5, postedResponse.Data.Tasks.Count);
        Assert.Equal(12, postedResponse.Data.StatusCounts!["all"]);
        Assert.Equal(12, postedResponse.Data.StatusCounts["claimed"]);

        var linkedResult = await controller.GetMyPostedTasks(
            page: 1,
            pageSize: 5,
            taskId: "DFY-ACTIVE-12",
            status: "claimed");
        var linkedResponse = Assert.IsType<ApiResponse<PaginatedResponse<TaskDto>>>(
            Assert.IsType<OkObjectResult>(linkedResult.Result).Value);
        Assert.Equal(3, linkedResponse.Data!.Page);
        Assert.Contains(linkedResponse.Data.Tasks, task => task.TaskId == "DFY-ACTIVE-12");

        var emptyStatusResult = await controller.GetMyPostedTasks(status: "posted");
        var emptyStatusResponse = Assert.IsType<ApiResponse<PaginatedResponse<TaskDto>>>(
            Assert.IsType<OkObjectResult>(emptyStatusResult.Result).Value);
        Assert.Equal(0, emptyStatusResponse.Data!.Count);
        Assert.Equal(12, emptyStatusResponse.Data.StatusCounts!["all"]);

        var activeController = CreateTasksController(context, runner.Id);
        var activeResult = await activeController.GetMyActiveTasks(page: 20, pageSize: 5);
        var activeApiResponse = Assert.IsType<ApiResponse<object>>(
            Assert.IsType<OkObjectResult>(activeResult.Result).Value);
        var activePage = JsonSerializer.SerializeToElement(activeApiResponse.Data);
        Assert.Equal(12, activePage.GetProperty("count").GetInt32());
        Assert.Equal(3, activePage.GetProperty("page").GetInt32());
        Assert.Equal(3, activePage.GetProperty("totalPages").GetInt32());
        Assert.Equal(2, activePage.GetProperty("tasks").GetArrayLength());

        var linkedActiveResult = await activeController.GetMyActiveTasks(
            page: 1,
            pageSize: 5,
            taskId: "DFY-ACTIVE-12");
        var linkedActiveApiResponse = Assert.IsType<ApiResponse<object>>(
            Assert.IsType<OkObjectResult>(linkedActiveResult.Result).Value);
        var linkedActivePage = JsonSerializer.SerializeToElement(linkedActiveApiResponse.Data);
        Assert.Equal(3, linkedActivePage.GetProperty("page").GetInt32());
        Assert.Contains(
            linkedActivePage.GetProperty("tasks").EnumerateArray(),
            task => task.GetProperty("taskId").GetString() == "DFY-ACTIVE-12");

        context.Tasks.AddRange(
            new TaskEntity
            {
                TaskId = "DFY-ACTION-PAYMENT",
                TaskDescription = "Payment action",
                Category = "Other",
                Area = "Pretoria",
                DateNeeded = DateTime.UtcNow.AddDays(1),
                Budget = 200m,
                PayoutAmount = 170m,
                PaymentStatus = "Pending",
                TaskStatus = "PendingPayment",
                CreatedByUserId = creator.Id
            },
            new TaskEntity
            {
                TaskId = "DFY-ACTION-CONFIRMATION",
                TaskDescription = "Confirmation action",
                Category = "Other",
                Area = "Pretoria",
                DateNeeded = DateTime.UtcNow.AddDays(1),
                Budget = 200m,
                PayoutAmount = 170m,
                PaymentStatus = "EscrowHeld",
                TaskStatus = "AwaitingConfirmation",
                CreatedByUserId = creator.Id
            });
        await context.SaveChangesAsync();

        var actionRequiredResult = await controller.GetMyPostedTasks(
            page: 1,
            pageSize: 1,
            status: "actionRequired");
        var actionRequiredResponse = Assert.IsType<ApiResponse<PaginatedResponse<TaskDto>>>(
            Assert.IsType<OkObjectResult>(actionRequiredResult.Result).Value);
        Assert.Equal(2, actionRequiredResponse.Data!.Count);
        Assert.Equal(2, actionRequiredResponse.Data.TotalPages);
        Assert.Equal(14, actionRequiredResponse.Data.StatusCounts!["all"]);
    }

    [Fact]
    public async System.Threading.Tasks.Task DuplicateClaim_IsRejected()
    {
        using var context = CreateContext(nameof(DuplicateClaim_IsRejected));
        var poster = CreateUser(1, "creator", true, "poster@test.com");
        var runner1 = CreateUser(2, "runner", true, "runner1@test.com");
        var runner2 = CreateUser(3, "runner", true, "runner2@test.com");
        context.Users.AddRange(poster, runner1, runner2);
        context.BankAccounts.AddRange(
            CreateVerifiedBankAccount(runner1.Id),
            CreateVerifiedBankAccount(runner2.Id));

        var task = new TaskEntity
        {
            TaskId = "DFY-2",
            TaskDescription = "Plumbing repair",
            Category = "Home",
            Area = "Durban",
            DateNeeded = DateTime.UtcNow.AddDays(4),
            Budget = 600m,
            CommissionAmount = 90m,
            PayoutAmount = 510m,
            PaymentStatus = "EscrowHeld",
            TaskStatus = "Posted",
            EscrowStatus = "held",
            CreatedByUserId = poster.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller1 = CreateTasksController(context, runner1.Id);
        var first = await controller1.ClaimTask(task.TaskId, new ClaimTaskRequest { HelperName = "Runner One", HelperContact = "0811111111", TermsAccepted = true });
        Assert.True(((ApiResponse<bool>)((OkObjectResult)first.Result!).Value!).Success);

        var controller2 = CreateTasksController(context, runner2.Id);
        var second = await controller2.ClaimTask(task.TaskId, new ClaimTaskRequest { HelperName = "Runner Two", HelperContact = "0822222222", TermsAccepted = true });
        var conflict = Assert.IsType<ConflictObjectResult>(second.Result);
        var response = Assert.IsType<ApiResponse<bool>>(conflict.Value);

        Assert.False(response.Success);
        Assert.Equal("TASK_ALREADY_CLAIMED", response.Error);
    }

    [Fact]
    public async System.Threading.Tasks.Task EscrowRelease_CreatesPendingPaystackPayout()
    {
        using var context = CreateContext(nameof(EscrowRelease_CreatesPendingPaystackPayout));
        var poster = CreateUser(1, "creator", true, "poster@test.com");
        var runner = CreateUser(2, "runner", true, "runner@test.com");
        context.Users.AddRange(poster, runner);

        var bankAccount = new BankAccount
        {
            UserId = runner.Id,
            BankName = "Test Bank",
            AccountNumber = "1234567890",
            BranchCode = "123456",
            ProviderReference = "RCP_TEST_DFY_3",
            IsActive = true,
            IsVerified = true,
            IsDefaultPayout = true,
            CreatedAt = DateTime.UtcNow
        };
        var olderEligibleBankAccount = new BankAccount
        {
            UserId = runner.Id,
            BankName = "Older verified bank",
            AccountNumber = "1234567891",
            AccountHolderName = "Runner User",
            AccountType = "Savings",
            ProviderReference = "RCP_OLDER_DFY_3",
            IsActive = true,
            IsVerified = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-2)
        };
        var legacyTestBankAccount = new BankAccount
        {
            UserId = runner.Id,
            BankName = "DFY E2E Test Bank",
            BankGroupId = "DFY-E2E",
            AccountNumber = "0000000000",
            AccountHolderName = "Runner User",
            BranchCode = "000000",
            AccountType = "Savings",
            IsVerified = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1)
        };

        context.BankAccounts.AddRange(legacyTestBankAccount, olderEligibleBankAccount, bankAccount);

        var task = new TaskEntity
        {
            TaskId = "DFY-3",
            TaskDescription = "Electrical fix",
            Category = "Home",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(3),
            Budget = 800m,
            CommissionAmount = 120m,
            PayoutAmount = 680m,
            PaymentStatus = "EscrowHeld",
            TaskStatus = "AwaitingConfirmation",
            EscrowStatus = "held",
            EscrowHoldUntil = DateTime.UtcNow.AddHours(-1),
            CreatedByUserId = poster.Id,
            AcceptedByUserId = runner.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var escrow = new EscrowService(context, NullLogger<EscrowService>.Instance);
        var released = await escrow.ReleaseEscrowAsync(task.Id);

        Assert.True(released);

        var payout = await context.Payouts.SingleAsync(p => p.TaskId == task.Id);
        var refreshedTask = await context.Tasks.SingleAsync(t => t.Id == task.Id);

        Assert.Equal(runner.Id, payout.RunnerId);
        Assert.Equal(bankAccount.Id, payout.BankAccountId);
        Assert.Equal(680m, payout.Amount);
        Assert.Equal("Pending", payout.Status);
        Assert.Equal("Paystack", payout.Provider);
        Assert.Equal("DFY-PAYOUT-DFY-3", payout.MerchantReference);

        Assert.Equal("released", refreshedTask.EscrowStatus);
        Assert.Equal("EscrowReleased", refreshedTask.PaymentStatus);
        Assert.Equal("PayoutPending", refreshedTask.TaskStatus);
        Assert.Equal("Pending", refreshedTask.PayoutStatus);
        Assert.Equal("DFY-PAYOUT-DFY-3", refreshedTask.PayoutReference);

        Assert.Empty(await context.WalletTransactions
            .Where(w => w.UserId == runner.Id)
            .ToListAsync());
    }

private static AppDbContext CreateContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;

        return new AppDbContext(options);
    }

    private sealed class FakePaystackPaymentProvider : IPaymentProvider
    {
        public Task<PaymentCreationResult> CreatePaymentAsync(
            TaskEntity task,
            User customer,
            CancellationToken cancellationToken = default)
        {
            var reference = string.IsNullOrWhiteSpace(task.PaymentReference)
                ? $"dfy_pay_{task.TaskId}".ToLowerInvariant()
                : task.PaymentReference;

            return System.Threading.Tasks.Task.FromResult(
                new PaymentCreationResult(
                    true,
                    $"https://checkout.paystack.test/{reference}",
                    reference,
                    null));
        }

        public Task<PaymentVerificationResult> VerifyPaymentAsync(
            string transactionReference,
            CancellationToken cancellationToken = default)
        {
            return System.Threading.Tasks.Task.FromResult(
                new PaymentVerificationResult(
                    true,
                    true,
                    "TEST-PAYSTACK-TRANSACTION",
                    "success",
                    500m,
                    transactionReference,
                    null));
        }

        public Task<RefundResult> CreateRefundAsync(
            string transactionId,
            decimal amount,
            string reason,
            CancellationToken cancellationToken = default)
        {
            return System.Threading.Tasks.Task.FromResult(
                new RefundResult(true, "REF-TEST", transactionId, amount, "processed", null));
        }

        public Task<IReadOnlyList<RefundStatusResult>> GetRefundsByTransactionIdAsync(
            string transactionId,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<RefundStatusResult>>(
                Array.Empty<RefundStatusResult>());

        public bool VerifyWebhookSignature(string rawBody, string? signature) => true;
    }

    private static TasksController CreateTasksController(AppDbContext context, int userId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-key-for-scenario-tests-32chars!!",
                ["BackendUrl"] = "https://example.test",
                ["FrontendUrl"] = "https://example.test",
            })
            .Build();

        var controller = new TasksController(
            context,
            new RulesEngine(context),
            new EscrowService(context, NullLogger<EscrowService>.Instance),
            new NoOpNotificationService(),
            configuration,
            new FakePaystackPaymentProvider(),
            new UserPolicyService());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                }, "TestAuth"))
            }
        };

        return controller;
    }

    private static User CreateUser(int id, string userType, bool profileCompleted, string email)
    {
        return new User
        {
            Id = id,
            FirstName = userType == "creator" ? "Poster" : "Runner",
            LastName = "User",
            Email = email,
            PhoneNumber = "0712345678",
            IdNumber = "8001015009087",
            DateOfBirth = new DateTime(1980, 1, 1),
            UserType = userType,
            ProfileCompleted = profileCompleted,
            Address = "123 Test Street",
            Roles = "User",
            PasswordHash = "hash",
            IsVerified = true,
            EmailVerified = true,
            IdentityVerificationStatus = userType == "runner" ? "passed" : "not_started",
            AuthenticatorTwoFactorEnabled = userType == "runner",
        };
    }

    private static BankAccount CreateVerifiedBankAccount(int userId) => new()
    {
        UserId = userId,
        BankName = "Test Bank",
        BankCode = "TEST",
        ProviderReference = $"RCP_TEST_{userId}",
        AccountNumber = "1234567890",
        AccountHolderName = "Test Runner",
        BranchCode = "123456",
        AccountType = "Savings",
        IsVerified = true,
        IsActive = true
    };

    private sealed class NoOpNotificationService : INotificationService
    {
        public TaskResult CreateNotificationAsync(int userId, string type, string title, string message, int? relatedTaskId = null) => TaskResult.CompletedTask;
        public TaskResult NotifyTaskClaimedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null) => TaskResult.CompletedTask;
        public TaskResult NotifyTaskCompletedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null) => TaskResult.CompletedTask;
        public TaskResult NotifyPaymentReleasedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null) => TaskResult.CompletedTask;
        public TaskResult NotifyPayoutCompletedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null) => TaskResult.CompletedTask;
        public TaskResult NotifyNewMessageAsync(int recipientId, string taskDescription, string senderName, int? taskId = null) => TaskResult.CompletedTask;
        public TaskResult NotifyAdminsAsync(string type, string title, string message, int? relatedTaskId = null) => TaskResult.CompletedTask;
    }
}
