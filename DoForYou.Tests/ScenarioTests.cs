using System.Security.Claims;
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
        var poster = new User
        {
            Id = 1,
            FirstName = "Jane",
            LastName = "Poster",
            Email = "poster@example.com",
            PhoneNumber = "0712345678",
            UserType = "creator",
            ProfileCompleted = true,
            Address = "123 Main Road",
            Roles = "User",
        };
        context.Users.Add(poster);
        await context.SaveChangesAsync();

        var controller = CreateTasksController(context, poster.Id);
        var request = new CreateTaskRequest
        {
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

        Assert.True(response.Success);
        Assert.Equal("Task created successfully. Complete payment to activate.", response.Message);
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
        var result = await controller.ClaimTask(task.TaskId, new ClaimTaskRequest { HelperName = "Runner Name", HelperContact = "0820000000" });
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<bool>>(ok.Value);

        var savedTask = await context.Tasks.SingleAsync(t => t.Id == task.Id);
        Assert.True(response.Success);
        Assert.Equal("Claimed", savedTask.TaskStatus);
        Assert.Equal(runner.Id, savedTask.AcceptedByUserId);
    }

    [Fact]
    public async System.Threading.Tasks.Task DuplicateClaim_IsRejected()
    {
        using var context = CreateContext(nameof(DuplicateClaim_IsRejected));
        var poster = CreateUser(1, "creator", true, "poster@test.com");
        var runner1 = CreateUser(2, "runner", true, "runner1@test.com");
        var runner2 = CreateUser(3, "runner", true, "runner2@test.com");
        context.Users.AddRange(poster, runner1, runner2);

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
        var first = await controller1.ClaimTask(task.TaskId, new ClaimTaskRequest { HelperName = "Runner One", HelperContact = "0811111111" });
        Assert.True(((ApiResponse<bool>)((OkObjectResult)first.Result!).Value!).Success);

        var controller2 = CreateTasksController(context, runner2.Id);
        var second = await controller2.ClaimTask(task.TaskId, new ClaimTaskRequest { HelperName = "Runner Two", HelperContact = "0822222222" });
        var ok = Assert.IsType<OkObjectResult>(second.Result);
        var response = Assert.IsType<ApiResponse<bool>>(ok.Value);

        Assert.False(response.Success);
        Assert.Equal("Task not available", response.Message);
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
            IsActive = true,
            IsVerified = true,
            CreatedAt = DateTime.UtcNow
        };
        context.BankAccounts.Add(bankAccount);

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
            TaskStatus = "Completed",
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
            UserType = userType,
            ProfileCompleted = profileCompleted,
            Address = "123 Test Street",
            Roles = "User",
            PasswordHash = "hash",
            IsVerified = true,
        };
    }

    private sealed class NoOpNotificationService : INotificationService
    {
        public TaskResult CreateNotificationAsync(int userId, string type, string title, string message, int? relatedTaskId = null) => TaskResult.CompletedTask;
        public TaskResult NotifyTaskClaimedAsync(int creatorId, string taskDescription, string runnerName) => TaskResult.CompletedTask;
        public TaskResult NotifyTaskCompletedAsync(int creatorId, string taskDescription, string runnerName) => TaskResult.CompletedTask;
        public TaskResult NotifyPaymentReleasedAsync(int runnerId, string taskDescription, decimal amount) => TaskResult.CompletedTask;
        public TaskResult NotifyNewMessageAsync(int recipientId, string taskDescription, string senderName, int? taskId = null) => TaskResult.CompletedTask;
        public TaskResult NotifyAdminsAsync(string type, string title, string message, int? relatedTaskId = null) => TaskResult.CompletedTask;
    }
}
