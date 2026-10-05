using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;

namespace DoForYou.API.Services;

public sealed class PayoutProcessorService(
    IServiceScopeFactory scopeFactory,
    ILogger<PayoutProcessorService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingPayoutsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in generic payout processor.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task ProcessPendingPayoutsAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("DFY_E2E_TEST_MODE"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("DFY E2E test mode enabled; skipping provider payout submission.");
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = scope.ServiceProvider.GetRequiredService<IPayoutProvider>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var staleCutoff = DateTime.UtcNow.AddMinutes(-10);
        var stalePayouts = await context.Payouts
            .Include(p => p.Task)
            .Where(p => p.Status == "Processing" &&
                        p.ProcessingAt.HasValue &&
                        p.ProcessingAt < staleCutoff &&
                        (p.NextAttemptAt == null || p.NextAttemptAt <= DateTime.UtcNow))
            .ToListAsync(cancellationToken);

        foreach (var payout in stalePayouts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileAsync(context, provider, payout, cancellationToken);
        }

        var payouts = await context.Payouts
            .Include(p => p.Task)
            .Include(p => p.BankAccount)
            .Where(p => p.Status == "Pending" &&
                        p.Task.PayoutStatus == "Pending" &&
                        (p.NextAttemptAt == null || p.NextAttemptAt <= DateTime.UtcNow))
            .OrderBy(p => p.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var payout in payouts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!payout.BankAccount.IsActive || !payout.BankAccount.IsVerified)
                continue;

            if (string.IsNullOrWhiteSpace(payout.BankAccount.BankGroupId))
            {
                payout.Status = "AwaitingBankDetails";
                payout.FailureReason = "Runner bank details are incomplete.";
                payout.Task.PayoutStatus = "AwaitingBankDetails";
                payout.Task.TaskStatus = "PayoutPending";
                payout.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
                continue;
            }

            var now = DateTime.UtcNow;
            var claimed = await context.Payouts
                .Where(p => p.Id == payout.Id &&
                            p.Status == "Pending" &&
                            p.Task.PayoutStatus == "Pending" &&
                            (p.NextAttemptAt == null || p.NextAttemptAt <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.Status, "Processing")
                    .SetProperty(p => p.ProcessingAt, now)
                    .SetProperty(p => p.LastAttemptAt, now)
                    .SetProperty(p => p.AttemptCount, p => p.AttemptCount + 1)
                    .SetProperty(p => p.UpdatedAt, now), cancellationToken);

            if (claimed != 1)
                continue;

            await context.Entry(payout).ReloadAsync(cancellationToken);
            await context.Entry(payout.Task).ReloadAsync(cancellationToken);
            payout.Task.PayoutStatus = "Processing";
            payout.Task.TaskStatus = "PayoutPending";
            payout.Task.UpdatedAt = now;
            await context.SaveChangesAsync(cancellationToken);

            var result = await provider.RequestPayoutAsync(
                payout,
                payout.BankAccount,
                cancellationToken);

            if (result.Success)
            {
                payout.ProviderReference = result.PayoutId;
                payout.Provider = provider.Name;
                payout.Status = "Processing";
                payout.NextAttemptAt = null;
                payout.ProcessingError = null;
                payout.UpdatedAt = DateTime.UtcNow;
                payout.Task.PayoutStatus = "Processing";
                payout.Task.PayoutReference = result.PayoutId ?? payout.MerchantReference;
                payout.Task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
                continue;
            }

            if (result.Retryable)
            {
                payout.Status = "Processing";
                payout.Provider = provider.Name;
                payout.ProcessingError = result.Error ?? "Provider payout request was ambiguous and requires reconciliation.";
                payout.NextAttemptAt = DateTime.UtcNow.AddMinutes(10);
                payout.UpdatedAt = DateTime.UtcNow;
                payout.Task.PayoutStatus = "Processing";
                payout.Task.TaskStatus = "PayoutPending";
                payout.Task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
                continue;
            }

            payout.Status = "Failed";
            payout.Provider = provider.Name;
            payout.FailureReason = result.Error;
            payout.UpdatedAt = DateTime.UtcNow;
            payout.Task.PayoutStatus = "Failed";
            payout.Task.TaskStatus = "PayoutFailed";
            payout.Task.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);

            logger.LogError("Payout {PayoutId} failed through {Provider}: {Reason}",
                payout.Id, provider.Name, result.Error);
        }
    }

    private static async Task ReconcileAsync(
        AppDbContext context,
        IPayoutProvider provider,
        Models.Payout payout,
        CancellationToken cancellationToken)
    {
        var reconciliation = await provider.GetPayoutByReferenceAsync(
            payout,
            cancellationToken);

        if (!reconciliation.Success)
        {
            payout.ProcessingError = reconciliation.Error ?? reconciliation.ErrorMessage ?? "Payout reconciliation is unresolved.";
            payout.NextAttemptAt = DateTime.UtcNow.AddMinutes(10);
            payout.UpdatedAt = DateTime.UtcNow;
            payout.Task.PayoutStatus = "Processing";
            payout.Task.TaskStatus = "PayoutPending";
            payout.Task.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!reconciliation.Found)
        {
            if (reconciliation.RetrySafe)
            {
                payout.Status = "Pending";
                payout.ProcessingError = null;
                payout.NextAttemptAt = DateTime.UtcNow;
                payout.UpdatedAt = DateTime.UtcNow;
                payout.Task.PayoutStatus = "Pending";
                payout.Task.TaskStatus = "PayoutPending";
                payout.Task.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        payout.Provider = provider.Name;
        payout.ProviderReference = reconciliation.PayoutId ?? payout.ProviderReference;
        payout.ProcessingError = reconciliation.ErrorMessage;
        payout.UpdatedAt = DateTime.UtcNow;

        switch (reconciliation.Status)
        {
            case ProviderPayoutStatus.Completed:
                MarkCompleted(payout);
                break;

            case ProviderPayoutStatus.Returned:
                if (!IsTerminalCompleted(payout))
                {
                    payout.Status = "Returned";
                    payout.FailureReason = reconciliation.ErrorMessage ?? $"{provider.Name} returned the payout.";
                    payout.NextAttemptAt = null;
                    payout.Task.PayoutStatus = "Returned";
                    payout.Task.PaymentStatus = "PayoutReturned";
                    payout.Task.TaskStatus = "PayoutReturned";
                }
                break;

            case ProviderPayoutStatus.Cancelled:
                if (!IsTerminalCompleted(payout))
                {
                    payout.Status = "Cancelled";
                    payout.FailureReason = reconciliation.ErrorMessage ?? $"{provider.Name} cancelled the payout.";
                    payout.NextAttemptAt = null;
                    payout.Task.PayoutStatus = "Cancelled";
                    payout.Task.TaskStatus = "PayoutCancelled";
                }
                break;

            case ProviderPayoutStatus.Failed:
                if (!IsTerminalCompleted(payout))
                {
                    payout.Status = "Failed";
                    payout.FailureReason = reconciliation.ErrorMessage ?? $"{provider.Name} failed the payout.";
                    payout.NextAttemptAt = null;
                    payout.Task.PayoutStatus = "Failed";
                    payout.Task.TaskStatus = "PayoutFailed";
                }
                break;

            default:
                payout.Status = "Processing";
                payout.NextAttemptAt = null;
                payout.Task.PayoutStatus = "Processing";
                payout.Task.TaskStatus = "PayoutPending";
                break;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static void MarkCompleted(Models.Payout payout)
    {
        var completedAt = payout.CompletedAt ?? DateTime.UtcNow;
        payout.Status = "Completed";
        payout.CompletedAt = completedAt;
        payout.FailureReason = null;
        payout.ProcessingError = null;
        payout.NextAttemptAt = null;
        payout.Task.PayoutStatus = "Completed";
        payout.Task.PayoutCompletedAt ??= completedAt;
        payout.Task.PaidToRunnerAt ??= completedAt;
        payout.Task.PaymentStatus = "PayoutCompleted";
        payout.Task.TaskStatus = "RunnerPaid";
        payout.Task.UpdatedAt = completedAt;
    }

    private static bool IsTerminalCompleted(Models.Payout payout) =>
        payout.Status == "Completed" ||
        payout.Task.PayoutStatus == "Completed" ||
        payout.Task.TaskStatus == "RunnerPaid";
}
