using System.Security.Claims;
using DoForYou.API.Controllers;
using DoForYou.API.Data;
using DoForYou.API.DTOs;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskEntity = DoForYou.API.Models.Task;

namespace DoForYou.Tests;

public class DisputeLifecycleTests
{
    [Fact]
    public async System.Threading.Tasks.Task EligibleDisputeFreezesHeldEscrowAndCannotBeRepeated()
    {
        using var context = CreateContext(nameof(EligibleDisputeFreezesHeldEscrowAndCannotBeRepeated));
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var task = CreateTask(creator.Id, runner.Id, "AwaitingConfirmation", "EscrowHeld", "held");
        context.Users.AddRange(creator, runner);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateController(context, creator.Id);
        var request = new RaiseDisputeRequest
        {
            TaskId = task.TaskId,
            Issue = "The work was not completed as agreed.",
            Category = "Incomplete work"
        };

        var result = await controller.RaiseDispute(request);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);

        Assert.True(response.Success, response.Message);
        Assert.Equal("disputed", task.EscrowStatus);
        Assert.Equal("DisputePending", task.PaymentStatus);
        Assert.Equal("AwaitingConfirmation", task.TaskStatus);
        var dispute = Assert.Single(context.Disputes);
        Assert.Equal("Open", dispute.Status);
        Assert.Equal(creator.Id, dispute.ReportedByUserId);

        var repeated = await controller.RaiseDispute(request);
        Assert.IsType<ConflictObjectResult>(repeated.Result);
        Assert.Single(context.Disputes);
    }

    [Theory]
    [InlineData("Posted", "EscrowHeld", "held")]
    [InlineData("Claimed", "EscrowHeld", "held")]
    [InlineData("RunnerPaid", "EscrowReleased", "released")]
    [InlineData("Cancelled", "Refunded", "refunded")]
    [InlineData("AwaitingConfirmation", "DisputePending", "disputed")]
    public async System.Threading.Tasks.Task IneligibleTaskStateCannotOpenDisputeOrMutateEscrow(
        string taskStatus,
        string paymentStatus,
        string escrowStatus)
    {
        using var context = CreateContext($"{nameof(IneligibleTaskStateCannotOpenDisputeOrMutateEscrow)}-{taskStatus}");
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var task = CreateTask(creator.Id, runner.Id, taskStatus, paymentStatus, escrowStatus);
        context.Users.AddRange(creator, runner);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateController(context, creator.Id);
        var result = await controller.RaiseDispute(new RaiseDisputeRequest
        {
            TaskId = task.TaskId,
            Issue = "The work or payment status is incorrect.",
            Category = "Other"
        });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Empty(await context.Disputes.ToListAsync());
        Assert.Equal(taskStatus, task.TaskStatus);
        Assert.Equal(paymentStatus, task.PaymentStatus);
        Assert.Equal(escrowStatus, task.EscrowStatus);
    }

    [Fact]
    public async System.Threading.Tasks.Task NonParticipantCannotFreezeAnotherTasksEscrow()
    {
        using var context = CreateContext(nameof(NonParticipantCannotFreezeAnotherTasksEscrow));
        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        var stranger = CreateUser(3, "runner", "stranger@test.com");
        var task = CreateTask(creator.Id, runner.Id, "AwaitingConfirmation", "EscrowHeld", "held");
        context.Users.AddRange(creator, runner, stranger);
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var controller = CreateController(context, stranger.Id);
        var result = await controller.RaiseDispute(new RaiseDisputeRequest
        {
            TaskId = task.TaskId,
            Issue = "An unrelated user is trying to dispute this task.",
            Category = "Abuse"
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.False(response.Success);
        Assert.Empty(await context.Disputes.ToListAsync());
        Assert.Equal("held", task.EscrowStatus);
        Assert.Equal("EscrowHeld", task.PaymentStatus);
    }

    private static AppDbContext CreateContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AppDbContext(options);
    }

    private static User CreateUser(int id, string type, string email) => new()
    {
        Id = id,
        FirstName = type == "creator" ? "Creator" : "Runner",
        LastName = "Test",
        Email = email,
        NormalizedEmail = email.ToLowerInvariant(),
        PhoneNumber = "0821234567",
        UserType = type,
        EmailVerified = true,
        IdentityVerificationStatus = type == "runner" ? "passed" : "not_started",
        AuthenticatorTwoFactorEnabled = type == "runner",
        PasswordHash = "test-hash",
        Roles = "User"
    };

    private static TaskEntity CreateTask(int creatorId, int runnerId, string taskStatus, string paymentStatus, string escrowStatus) => new()
    {
        TaskId = "DFY-DISPUTE-" + Guid.NewGuid().ToString("N")[..8],
        TaskName = "Test task",
        TaskDescription = "A task used to test dispute escrow transitions.",
        Category = "Other",
        Area = "Pretoria",
        DateNeeded = DateTime.UtcNow.AddDays(1),
        Budget = 200m,
        CommissionAmount = 30m,
        PayoutAmount = 170m,
        CreatedByUserId = creatorId,
        AcceptedByUserId = runnerId,
        TaskStatus = taskStatus,
        PaymentStatus = paymentStatus,
        EscrowStatus = escrowStatus,
        EscrowHoldUntil = DateTime.UtcNow.AddHours(23),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        CompletedAt = taskStatus == "AwaitingConfirmation" ? DateTime.UtcNow : null
    };

    private static DisputesController CreateController(AppDbContext context, int userId)
    {
        var controller = new DisputesController(
            context,
            new EscrowService(context, NullLogger<EscrowService>.Instance),
            new NoOpNotificationService(),
            new FakePaymentProvider());
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "TestAuth"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private sealed class FakePaymentProvider : IPaymentProvider
    {
        public Task<PaymentCreationResult> CreatePaymentAsync(
            TaskEntity task,
            User customer,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(new PaymentCreationResult(true, "https://checkout.test/payment", "DFY_TEST", null));

        public Task<PaymentVerificationResult> VerifyPaymentAsync(
            string transactionReference,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(new PaymentVerificationResult(true, true, "TX-TEST", "success", 200m, transactionReference, null));

        public Task<RefundResult> CreateRefundAsync(
            string transactionId,
            decimal amount,
            string reason,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(new RefundResult(true, "REF-TEST", transactionId, amount, "processed", null));

        public Task<IReadOnlyList<RefundStatusResult>> GetRefundsByTransactionIdAsync(
            string transactionId,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<RefundStatusResult>>(Array.Empty<RefundStatusResult>());

        public bool VerifyWebhookSignature(string rawBody, string? signature) => true;
    }

    private sealed class NoOpNotificationService : INotificationService
    {
        public Task CreateNotificationAsync(int userId, string type, string title, string message, int? relatedTaskId = null) => Task.CompletedTask;
        public Task NotifyTaskClaimedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null) => Task.CompletedTask;
        public Task NotifyTaskCompletedAsync(int creatorId, string taskDescription, string runnerName, int? taskId = null) => Task.CompletedTask;
        public Task NotifyPaymentReleasedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null) => Task.CompletedTask;
        public Task NotifyPayoutCompletedAsync(int runnerId, string taskDescription, decimal amount, int? taskId = null) => Task.CompletedTask;
        public Task NotifyNewMessageAsync(int recipientId, string taskDescription, string senderName, int? taskId = null) => Task.CompletedTask;
        public Task NotifyAdminsAsync(string type, string title, string message, int? relatedTaskId = null) => Task.CompletedTask;
    }
}
