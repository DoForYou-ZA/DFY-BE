using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using DoForYou.API.Data;
using DoForYou.API.Models;

namespace DoForYou.API.Services;

public interface IEscrowService
{
    Task<bool> ReleaseEscrowAsync(int taskId, bool force = false);
    Task<int> AutoReleaseExpiredEscrowsAsync();
    decimal CalculateCommission(decimal amount);
}

public class EscrowService : IEscrowService
{
    private readonly AppDbContext _context;
    private readonly ILogger<EscrowService> _logger;

    public EscrowService(AppDbContext context, ILogger<EscrowService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<bool> ReleaseEscrowAsync(int taskId, bool force = false)
    {
        IDbContextTransaction? transaction = null;
        if (_context.Database.IsRelational())
            transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            var task = await _context.Tasks.Include(t => t.AcceptedByUser).FirstOrDefaultAsync(t => t.Id == taskId);
            if (task == null || task.AcceptedByUserId == null) return false;
            if (task.EscrowStatus != "held" || task.PaymentStatus != "EscrowHeld") return false;

            // Only one confirmation path may claim the AwaitingConfirmation row.
            // ExecuteUpdate makes the state transition conditional in the database,
            // so a creator confirmation and the 24-hour worker cannot both win.
            if (task.TaskStatus == "RunnerPaid")
                return true;

            if (task.TaskStatus != "AwaitingConfirmation")
                return false;

            if (!force && (!task.EscrowHoldUntil.HasValue || task.EscrowHoldUntil > DateTime.UtcNow))
                return false;

            var eligibleTask = _context.Tasks
                .Where(t => t.Id == task.Id &&
                            t.TaskStatus == "AwaitingConfirmation" &&
                            t.EscrowStatus == "held" &&
                            t.PaymentStatus == "EscrowHeld" &&
                            (force || (t.EscrowHoldUntil.HasValue && t.EscrowHoldUntil <= DateTime.UtcNow)));
            int claimed;
            if (_context.Database.IsRelational())
            {
                claimed = await eligibleTask.ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.TaskStatus, "PayoutPending")
                    .SetProperty(t => t.UpdatedAt, DateTime.UtcNow));
            }
            else
            {
                var testTask = await eligibleTask.FirstOrDefaultAsync();
                if (testTask == null)
                    return false;

                testTask.TaskStatus = "PayoutPending";
                testTask.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                claimed = 1;
            }

            if (claimed != 1)
                return false;

            // Refresh the tracked task after the atomic state claim.
            _context.ChangeTracker.Clear();
            task = await _context.Tasks.Include(t => t.AcceptedByUser).FirstOrDefaultAsync(t => t.Id == taskId);
            if (task == null || task.AcceptedByUserId == null) return false;

            // Keep active/processing/completed payout records idempotent. A terminal
            // Failed/Returned payout is historical and may be followed by a new
            // payout attempt after the underlying issue is corrected.
            var existingPayout = await _context.Payouts
                .Where(p => p.TaskId == task.Id)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (existingPayout != null &&
                existingPayout.Status is not ("Failed" or "Returned" or "Cancelled"))
            {
                if (transaction != null) await transaction.CommitAsync();
                return true;
            }

            var bankAccount = await _context.BankAccounts
                .Where(b =>
                    b.UserId == task.AcceptedByUserId.Value &&
                    b.IsActive &&
                    b.IsVerified &&
                    !string.IsNullOrWhiteSpace(b.ProviderReference))
                .OrderByDescending(b => b.IsDefaultPayout)
                .ThenBy(b => b.CreatedAt)
                .ThenBy(b => b.Id)
                .FirstOrDefaultAsync();
            if (bankAccount == null)
            {
                if (transaction != null) await transaction.RollbackAsync();
                _logger.LogWarning("Cannot release task {TaskId}: runner {RunnerId} has no active verified bank account.", task.TaskId, task.AcceptedByUserId.Value);
                return false;
            }

            // A new provider submission after a terminal payout failure/return
            // gets a fresh merchant reference so the prior provider transaction
            // remains immutable and auditable.
            var merchantReference = existingPayout == null
                ? $"DFY-PAYOUT-{task.TaskId}"
                : $"DFY-PAYOUT-{task.TaskId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var payout = new Payout
            {
                TaskId = task.Id,
                RunnerId = task.AcceptedByUserId.Value,
                BankAccountId = bankAccount.Id,
                Amount = task.PayoutAmount,
                Status = "Pending",
                Provider = "Paystack",
                MerchantReference = merchantReference,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Payouts.Add(payout);
            task.EscrowStatus = "released";
            task.PaymentStatus = "EscrowReleased";
            task.TaskStatus = "PayoutPending";
            task.PayoutStatus = "Pending";
            task.PayoutReference = merchantReference;
            task.PayoutInitiatedAt = DateTime.UtcNow;
            task.PaidToRunnerAt = null;
            task.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            if (transaction != null) await transaction.CommitAsync();
            _logger.LogInformation("Escrow released for task {TaskId}: R{Payout}", task.TaskId, task.PayoutAmount);
            return true;
        }
        catch (Exception ex)
        {
            if (transaction != null) await transaction.RollbackAsync();
            _logger.LogError(ex, "Error releasing escrow for task {TaskId}", taskId);
            return false;
        }
    }

    public async Task<int> AutoReleaseExpiredEscrowsAsync()
    {
        var expiredTasks = await _context.Tasks.Where(t => t.TaskStatus == "AwaitingConfirmation" && t.EscrowStatus == "held" && t.PaymentStatus == "EscrowHeld" && t.EscrowHoldUntil.HasValue && t.EscrowHoldUntil <= DateTime.UtcNow).ToListAsync();
        int count = 0;
        foreach (var task in expiredTasks)
            if (await ReleaseEscrowAsync(task.Id)) count++;
        return count;
    }

    public decimal CalculateCommission(decimal amount) => Math.Max(amount * 0.15m, 5.00m);
}
