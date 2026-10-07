using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;
using DoForYou.API.Models;
using Task = System.Threading.Tasks.Task;

namespace DoForYou.API.Services;

public sealed class PayoutProcessorService(
    IServiceScopeFactory scopeFactory,
    ILogger<PayoutProcessorService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDuePayouts(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Paystack payout processor cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task ProcessDuePayouts(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = scope.ServiceProvider.GetRequiredService<IPayoutProvider>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var payouts = await context.Payouts
            .Include(p => p.BankAccount)
            .Include(p => p.Task)
            .Where(p =>
                p.Provider == "Paystack" &&
                (p.Status == "Pending" ||
                 p.Status == "Processing" ||
                 p.Status == "AwaitingFunds") &&
                (p.NextAttemptAt == null || p.NextAttemptAt <= DateTime.UtcNow))
            .OrderBy(p => p.CreatedAt)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var payout in payouts)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (payout.Status is "Pending" or "AwaitingFunds")
                await SubmitPendingPayout(
                    context,
                    provider,
                    payout,
                    cancellationToken);
            else
                await ReconcilePayout(
                    context,
                    provider,
                    notifications,
                    payout,
                    cancellationToken);
        }
    }

    private async Task SubmitPendingPayout(
        AppDbContext context,
        IPayoutProvider provider,
        Payout payout,
        CancellationToken cancellationToken)
    {
        var claimed = await context.Payouts
            .Where(p => p.Id == payout.Id &&
                        (p.Status == "Pending" || p.Status == "AwaitingFunds"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.Status, "Processing")
                .SetProperty(p => p.ProcessingAt, DateTime.UtcNow)
                .SetProperty(p => p.LastAttemptAt, DateTime.UtcNow)
                .SetProperty(p => p.AttemptCount, p => p.AttemptCount + 1)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow),
                cancellationToken);

        if (claimed != 1)
            return;

        context.ChangeTracker.Clear();

        payout = await context.Payouts
            .Include(p => p.BankAccount)
            .Include(p => p.Task)
            .FirstOrDefaultAsync(p => p.Id == payout.Id, cancellationToken);

        if (payout == null)
            return;

        if (!payout.BankAccount.IsVerified ||
            !payout.BankAccount.IsActive)
        {
            payout.Status = "AwaitingBankDetails";
            payout.FailureReason = "Runner bank account is no longer verified or active.";
            payout.NextAttemptAt = null;
            payout.Task.PayoutStatus = "AwaitingBankDetails";
            payout.Task.TaskStatus = "PayoutPending";
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(payout.BankAccount.ProviderReference))
        {
            payout.Status = "AwaitingBankDetails";
            payout.FailureReason = "Runner bank account is missing its Paystack recipient reference.";
            payout.NextAttemptAt = null;
            payout.Task.PayoutStatus = "AwaitingBankDetails";
            payout.Task.TaskStatus = "PayoutPending";
            payout.UpdatedAt = DateTime.UtcNow;
            payout.Task.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        var result = await provider.CreatePayoutAsync(
            payout,
            payout.BankAccount,
            cancellationToken);

        if (result.Success)
        {
            payout.ProviderReference = result.ProviderReference;
            payout.Status = "Processing";
            payout.FailureReason = null;
            payout.ProcessingError = null;
            payout.NextAttemptAt = DateTime.UtcNow.AddMinutes(2);
            payout.Task.PayoutStatus = "Processing";
            payout.Task.TaskStatus = "PayoutPending";
        }
        else if (result.Retryable)
        {
            // Balance shortages are recoverable: wait for funds and retry the exact
            // same merchant reference later. Other ambiguous provider/network errors
            // remain Processing and are reconciled before any new submission.
            var awaitingFunds = result.Error?.Contains("balance", StringComparison.OrdinalIgnoreCase) == true &&
                                (result.Error.Contains("not enough", StringComparison.OrdinalIgnoreCase) ||
                                 result.Error.Contains("insufficient", StringComparison.OrdinalIgnoreCase));

            payout.Status = awaitingFunds ? "AwaitingFunds" : "Processing";
            payout.ProcessingError = result.Error;
            payout.NextAttemptAt = DateTime.UtcNow.AddMinutes(awaitingFunds ? 5 : 5);
            payout.Task.PayoutStatus = awaitingFunds ? "AwaitingFunds" : "Processing";
        }
        else
        {
            payout.Status = "Failed";
            payout.FailureReason = result.Error;
            payout.ProcessingError = null;
            payout.NextAttemptAt = null;
            payout.Task.PayoutStatus = "Failed";
            payout.Task.TaskStatus = "PayoutFailed";
        }

        payout.UpdatedAt = DateTime.UtcNow;
        payout.Task.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcilePayout(
        AppDbContext context,
        IPayoutProvider provider,
        INotificationService notifications,
        Payout payout,
        CancellationToken cancellationToken)
    {
        var result = await provider.GetPayoutStatusAsync(
            payout,
            cancellationToken);

        if (!result.Success)
        {
            payout.ProcessingError = result.Error ?? "Paystack payout reconciliation failed.";
            payout.NextAttemptAt = DateTime.UtcNow.AddMinutes(5);
            payout.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!result.Found)
        {
            // Paystack recommends reusing the same reference when a transfer request
            // is non-conclusive. Returning to Pending is safe because CreatePayoutAsync
            // sends the exact same reference.
            payout.Status = "Pending";
            payout.ProcessingError = null;
            payout.NextAttemptAt = DateTime.UtcNow.AddMinutes(2);
            payout.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.ProviderReference))
            payout.ProviderReference = result.ProviderReference;

        var status = result.Status?.ToLowerInvariant();

        switch (status)
        {
            case "success":
                payout.Status = "Completed";
                payout.CompletedAt = DateTime.UtcNow;
                payout.NextAttemptAt = null;
                payout.FailureReason = null;
                payout.ProcessingError = null;

                if (payout.Task.TaskStatus != "RunnerPaid")
                {
                    payout.Task.PayoutStatus = "Completed";
                    payout.Task.TaskStatus = "RunnerPaid";
                    payout.Task.PayoutCompletedAt = DateTime.UtcNow;
                    payout.Task.PaidToRunnerAt = DateTime.UtcNow;
                    payout.Task.UpdatedAt = DateTime.UtcNow;

                    await notifications.NotifyPaymentReleasedAsync(
                        payout.RunnerId,
                        payout.Task.TaskDescription,
                        payout.Amount,
                        payout.Task.Id);
                }
                break;

            case "failed":
                payout.Status = "Failed";
                payout.FailureReason = "Paystack transfer failed.";
                payout.NextAttemptAt = null;
                payout.ProcessingError = null;
                payout.Task.PayoutStatus = "Failed";
                payout.Task.TaskStatus = "PayoutFailed";
                payout.Task.UpdatedAt = DateTime.UtcNow;
                break;

            case "reversed":
                payout.Status = "Returned";
                payout.FailureReason = "Paystack reversed the runner payout.";
                payout.NextAttemptAt = null;
                payout.ProcessingError = null;
                payout.Task.PayoutStatus = "Returned";
                payout.Task.TaskStatus = "PayoutFailed";
                payout.Task.UpdatedAt = DateTime.UtcNow;
                break;

            default:
                payout.Status = "Processing";
                payout.NextAttemptAt = DateTime.UtcNow.AddMinutes(2);
                payout.ProcessingError = null;
                payout.Task.PayoutStatus = "Processing";
                payout.Task.TaskStatus = "PayoutPending";
                payout.Task.UpdatedAt = DateTime.UtcNow;
                break;
        }

        payout.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }
}
