using DoForYou.API.Data;
using DoForYou.API.Models;
using DoForYou.API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DoForYou.Tests;

public class CreatorConfirmation24HourTests
{
    [Fact]
    public async System.Threading.Tasks.Task ExpiredCreatorConfirmation_AutoReleasesEscrowWithoutOzowPayment()
    {
        using var context = CreateContext(nameof(ExpiredCreatorConfirmation_AutoReleasesEscrowWithoutOzowPayment));

        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        context.Users.AddRange(creator, runner);

        context.BankAccounts.Add(new BankAccount
        {
            UserId = runner.Id,
            BankName = "Test Bank",
            AccountNumber = "1234567890",
            BranchCode = "123456",
            IsActive = true,
            IsVerified = true,
            CreatedAt = DateTime.UtcNow
        });

        var task = new DoForYou.API.Models.Task
        {
            Id = 100,
            TaskId = "DFY-TEST-24H-001",
            TaskName = "24-hour confirmation test",
            TaskDescription = "Test task for creator confirmation expiry.",
            Category = "Home",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(2),
            Budget = 800m,
            CommissionAmount = 120m,
            PayoutAmount = 680m,
            PaymentStatus = "EscrowHeld",
            TaskStatus = "AwaitingConfirmation",
            EscrowStatus = "held",
            EscrowHoldUntil = DateTime.UtcNow.AddMinutes(-1),
            CreatedByUserId = creator.Id,
            AcceptedByUserId = runner.Id,
            CompletedAt = DateTime.UtcNow.AddHours(-24).AddMinutes(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-24)
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        // Deliberately do not invoke IOzowPaymentService. The test starts after
        // payment is already secured in escrow and verifies the 24-hour release path.
        var escrow = new EscrowService(context, NullLogger<EscrowService>.Instance);

        var released = await escrow.ReleaseEscrowAsync(task.Id);

        Assert.True(released);

        var savedTask = await context.Tasks.SingleAsync(t => t.Id == task.Id);
        var payout = await context.Payouts.SingleAsync(p => p.TaskId == task.Id);

        Assert.Equal("PayoutPending", savedTask.TaskStatus);
        Assert.Equal("EscrowReleased", savedTask.PaymentStatus);
        Assert.Equal("released", savedTask.EscrowStatus);
        Assert.Equal("Pending", savedTask.PayoutStatus);
        Assert.Equal("DFY-PAYOUT-DFY-TEST-24H-001", savedTask.PayoutReference);

        Assert.Equal(runner.Id, payout.RunnerId);
        Assert.Equal(680m, payout.Amount);
        Assert.Equal("Pending", payout.Status);
        Assert.Equal("Ozow", payout.Provider);
    }

    [Fact]
    public async System.Threading.Tasks.Task ActiveCreatorConfirmation_DoesNotReleaseBefore24Hours()
    {
        using var context = CreateContext(nameof(ActiveCreatorConfirmation_DoesNotReleaseBefore24Hours));

        var creator = CreateUser(1, "creator", "creator@test.com");
        var runner = CreateUser(2, "runner", "runner@test.com");
        context.Users.AddRange(creator, runner);

        context.BankAccounts.Add(new BankAccount
        {
            UserId = runner.Id,
            BankName = "Test Bank",
            AccountNumber = "1234567890",
            BranchCode = "123456",
            IsActive = true,
            IsVerified = true,
            CreatedAt = DateTime.UtcNow
        });

        var task = new DoForYou.API.Models.Task
        {
            Id = 101,
            TaskId = "DFY-TEST-24H-002",
            TaskName = "24-hour hold test",
            TaskDescription = "Test task that must remain held before expiry.",
            Category = "Home",
            Area = "Pretoria",
            DateNeeded = DateTime.UtcNow.AddDays(2),
            Budget = 500m,
            CommissionAmount = 75m,
            PayoutAmount = 425m,
            PaymentStatus = "EscrowHeld",
            TaskStatus = "AwaitingConfirmation",
            EscrowStatus = "held",
            EscrowHoldUntil = DateTime.UtcNow.AddHours(23),
            CreatedByUserId = creator.Id,
            AcceptedByUserId = runner.Id,
            CompletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var escrow = new EscrowService(context, NullLogger<EscrowService>.Instance);

        var released = await escrow.ReleaseEscrowAsync(task.Id);

        Assert.False(released);
        Assert.Equal("AwaitingConfirmation", (await context.Tasks.SingleAsync(t => t.Id == task.Id)).TaskStatus);
        Assert.Empty(await context.Payouts.Where(p => p.TaskId == task.Id).ToListAsync());
    }

    private static AppDbContext CreateContext(string name)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;

        return new AppDbContext(options);
    }

    private static User CreateUser(int id, string type, string email)
    {
        return new User
        {
            Id = id,
            FirstName = type == "creator" ? "Creator" : "Runner",
            LastName = "Test",
            Email = email,
            PhoneNumber = "0712345678",
            UserType = type,
            ProfileCompleted = true,
            Address = "123 Test Street",
            Roles = "User",
            PasswordHash = "test-hash",
            IsVerified = true
        };
    }
}
