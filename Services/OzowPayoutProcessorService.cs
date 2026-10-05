using Microsoft.EntityFrameworkCore;
using DoForYou.API.Data;

namespace DoForYou.API.Services;

public class OzowPayoutProcessorService(
    IServiceScopeFactory scopeFactory,
    ILogger<OzowPayoutProcessorService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval =
        TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await Task.Delay(
            TimeSpan.FromSeconds(15),
            stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingPayoutsAsync(
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Unexpected error in Ozow payout processor.");
            }

            await Task.Delay(
                Interval,
                stoppingToken);
        }
    }

    private async Task ProcessPendingPayoutsAsync(
        CancellationToken cancellationToken)
    {
        // Staging E2E mode must exercise escrow release and payout queuing
        // without submitting any payout to Ozow. The payout record remains
        // Pending so the E2E can assert the exact hand-off state.
        if (string.Equals(Environment.GetEnvironmentVariable("DFY_E2E_TEST_MODE"), "true", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("DFY E2E test mode enabled; skipping Ozow payout submission.");
            return;
        }

        using var scope =
            scopeFactory.CreateScope();

        var context =
            scope.ServiceProvider
                .GetRequiredService<AppDbContext>();

        var payoutService =
            scope.ServiceProvider
                .GetRequiredService<IOzowPayoutService>();

        // Reconcile ambiguous Processing payouts before any retry.
        //
        // IMPORTANT:
        // A request can reach Ozow successfully while DFY loses the
        // response. Never convert an ambiguous Processing payout directly
        // back to Pending because that can submit the same payout twice.
        var staleCutoff = DateTime.UtcNow.AddMinutes(-10);

        var stalePayouts =
            await context.Payouts
                .Include(p => p.Task)
                .Where(p =>
                    p.Status == "Processing" &&
                    p.ProcessingAt.HasValue &&
                    p.ProcessingAt < staleCutoff &&
                    (
                        p.NextAttemptAt == null ||
                        p.NextAttemptAt <= DateTime.UtcNow
                    ))
                .ToListAsync(cancellationToken);

        foreach (var stale in stalePayouts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var reconciliation =
                await payoutService.GetPayoutByReferenceAsync(
                    stale,
                    cancellationToken);

            if (reconciliation.Success &&
                reconciliation.Found &&
                !string.IsNullOrWhiteSpace(
                    reconciliation.PayoutId))
            {
                // Ozow already knows about this payout.
                //
                // Adopt the provider reference first. From this point
                // onward, the provider's actual status determines the
                // DFY state. Never treat "found" as automatically
                // Processing and never submit a second payout merely
                // because reconciliation found an existing record.

                stale.ProviderReference =
                    reconciliation.PayoutId;

                stale.Task.PayoutReference =
                    reconciliation.PayoutId;

                var providerStatus =
                    reconciliation.Status;

                var providerSubStatus =
                    reconciliation.SubStatus;

                stale.ProcessingError =
                    reconciliation.ErrorMessage;

                stale.UpdatedAt =
                    DateTime.UtcNow;

                stale.Task.UpdatedAt =
                    DateTime.UtcNow;

                if (providerStatus == 5)
                {
                    // PayoutComplete.
                    var completedAt =
                        stale.CompletedAt ?? DateTime.UtcNow;

                    stale.Status =
                        "Completed";

                    stale.CompletedAt =
                        completedAt;

                    stale.FailureReason =
                        null;

                    stale.ProcessingError =
                        null;

                    stale.NextAttemptAt =
                        null;

                    stale.Task.PayoutStatus =
                        "Completed";

                    stale.Task.PayoutCompletedAt =
                        stale.Task.PayoutCompletedAt ?? completedAt;

                    stale.Task.PaidToRunnerAt =
                        stale.Task.PaidToRunnerAt ?? completedAt;

                    stale.Task.PaymentStatus =
                        "PayoutCompleted";

                    stale.Task.TaskStatus =
                        "RunnerPaid";
                }
                else if (providerStatus == 90)
                {
                    // Completed is terminal. Never downgrade a payout after
                    // DFY has recorded the runner as paid.
                    if (stale.Status == "Completed" ||
                        stale.Task.PayoutStatus == "Completed" ||
                        stale.Task.TaskStatus == "RunnerPaid")
                    {
                        stale.UpdatedAt = DateTime.UtcNow;
                        await context.SaveChangesAsync(cancellationToken);
                        continue;
                    }

                    // PayoutReturned.
                    stale.Status =
                        "Returned";

                    stale.FailureReason =
                        $"Ozow payout returned. SubStatus={providerSubStatus}.";

                    stale.ProcessingError =
                        null;

                    stale.NextAttemptAt =
                        null;

                    stale.Task.PayoutStatus =
                        "Returned";

                    stale.Task.PaymentStatus =
                        "PayoutReturned";

                    // Preserve historical RunnerPaid if Ozow returned
                    // a payout after it was genuinely completed.
                    if (stale.Task.TaskStatus != "RunnerPaid")
                    {
                        stale.Task.TaskStatus =
                            "PayoutReturned";
                    }
                }
                else if (providerStatus == 99)
                {
                    // Completed is terminal. Never downgrade a payout after
                    // DFY has recorded the runner as paid.
                    if (stale.Status == "Completed" ||
                        stale.Task.PayoutStatus == "Completed" ||
                        stale.Task.TaskStatus == "RunnerPaid")
                    {
                        stale.UpdatedAt = DateTime.UtcNow;
                        await context.SaveChangesAsync(cancellationToken);
                        continue;
                    }

                    // PayoutCancelled.
                    stale.Status =
                        "Cancelled";

                    stale.FailureReason =
                        $"Ozow payout cancelled. SubStatus={providerSubStatus}.";

                    stale.ProcessingError =
                        null;

                    stale.NextAttemptAt =
                        null;

                    stale.Task.PayoutStatus =
                        "Cancelled";

                    if (stale.Task.TaskStatus != "RunnerPaid")
                    {
                        stale.Task.TaskStatus =
                            "PayoutCancelled";
                    }
                }
                else if (providerStatus == 4 &&
                         providerSubStatus == 403)
                {
                    // Insufficient Ozow float.
                    // Ozow says this must not be resubmitted.
                    stale.Status =
                        "AwaitingFunds";

                    stale.FailureReason =
                        "Ozow payout is awaiting merchant float.";

                    stale.ProcessingError =
                        null;

                    stale.NextAttemptAt =
                        null;

                    stale.Task.PayoutStatus =
                        "AwaitingFunds";

                    stale.Task.TaskStatus =
                        "PayoutPending";
                }
                else if (providerStatus == 4 &&
                         providerSubStatus == 405)
                {
                    // Invalid destination account.
                    // Bank details must be corrected before another
                    // payout submission is attempted.
                    stale.Status =
                        "AwaitingBankDetails";

                    stale.FailureReason =
                        "Ozow rejected the destination bank account.";

                    stale.ProcessingError =
                        null;

                    stale.NextAttemptAt =
                        null;

                    stale.Task.PayoutStatus =
                        "AwaitingBankDetails";

                    stale.Task.TaskStatus =
                        "PayoutPending";
                }
                else if (providerStatus == 4 &&
                         (providerSubStatus == 401 ||
                          providerSubStatus == 402 ||
                          providerSubStatus == 404))
                {
                    // Ozow has confirmed that the existing payout reached
                    // a resubmittable processing-error state.
                    //
                    // Reconciliation has now established the provider
                    // state, so returning to Pending is safe. The next
                    // processor cycle may intentionally submit the payout
                    // again using the same DFY merchant reference.

                    stale.Status =
                        "Pending";

                    stale.FailureReason =
                        $"Ozow payout can be resubmitted. SubStatus={providerSubStatus}.";

                    stale.ProcessingError =
                        null;

                    stale.NextAttemptAt =
                        DateTime.UtcNow;

                    stale.Task.PayoutStatus =
                        "Pending";

                    stale.Task.TaskStatus =
                        "PayoutPending";
                }
                else if (providerStatus == 4)
                {
                    // Unknown processing-error substatus.
                    // Never blindly resubmit an unknown provider state.
                    stale.Status =
                        "Failed";

                    stale.FailureReason =
                        $"Ozow payout processing error. SubStatus={providerSubStatus}.";

                    stale.ProcessingError =
                        null;

                    stale.NextAttemptAt =
                        null;

                    stale.Task.PayoutStatus =
                        "Failed";

                    stale.Task.TaskStatus =
                        "PayoutFailed";
                }
                else
                {
                    // 1/2/3/6/7 and any other non-final provider state.
                    stale.Status =
                        "Processing";

                    stale.ProcessingError =
                        reconciliation.ErrorMessage;

                    stale.NextAttemptAt =
                        null;

                    stale.Task.PayoutStatus =
                        "Processing";

                    stale.Task.TaskStatus =
                        "PayoutPending";
                }

                await context.SaveChangesAsync(
                    cancellationToken);

                logger.LogInformation(
                    "Reconciled Ozow payout {PayoutId} for DFY payout {DfyPayoutId}. ProviderStatus={Status}, SubStatus={SubStatus}.",
                    reconciliation.PayoutId,
                    stale.Id,
                    providerStatus,
                    providerSubStatus);

                continue;
            }

            if (reconciliation.Success &&
                !reconciliation.Found &&
                reconciliation.RetrySafe)
            {
                // Ozow explicitly returned no payout for the merchant
                // reference. This is the only safe path back to Pending.
                stale.Status = "Pending";
                stale.Task.PayoutStatus = "Pending";
                stale.ProcessingError = null;
                stale.NextAttemptAt = DateTime.UtcNow;
                stale.UpdatedAt = DateTime.UtcNow;
                stale.Task.UpdatedAt = DateTime.UtcNow;

                await context.SaveChangesAsync(
                    cancellationToken);

                logger.LogWarning(
                    "Ozow reconciliation found no payout for DFY payout {PayoutId}; retry is safe.",
                    stale.Id);

                continue;
            }

            // Unknown/error during reconciliation.
            // Keep Processing and DO NOT submit another payout.
            stale.ProcessingError =
                reconciliation.Error ??
                reconciliation.ErrorMessage ??
                "Ozow payout reconciliation is unresolved.";

            stale.NextAttemptAt =
                DateTime.UtcNow.AddMinutes(10);

            stale.UpdatedAt =
                DateTime.UtcNow;

            stale.Task.PayoutStatus =
                "Processing";

            stale.Task.UpdatedAt =
                DateTime.UtcNow;

            await context.SaveChangesAsync(
                cancellationToken);

            logger.LogWarning(
                "Ozow payout {PayoutId} remains Processing because reconciliation was unresolved. No retry submitted.",
                stale.Id);
        }

        var payouts =
            await context.Payouts
                .Include(p => p.Task)
                .Include(p => p.BankAccount)
                .Where(p =>
                    p.Status == "Pending" &&
                    p.Task.PayoutStatus == "Pending" &&
                    (
                        p.NextAttemptAt == null ||
                        p.NextAttemptAt <= DateTime.UtcNow
                    ))
                .OrderBy(p => p.CreatedAt)
                .Take(10)
                .ToListAsync(cancellationToken);

        foreach (var payout in payouts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!payout.BankAccount.IsActive ||
                !payout.BankAccount.IsVerified)
            {
                logger.LogWarning(
                    "Payout {PayoutId} waiting for verified bank account.",
                    payout.Id);

                continue;
            }

            if (string.IsNullOrWhiteSpace(
                    payout.BankAccount.BankGroupId))
            {
                logger.LogWarning(
                    "Payout {PayoutId} waiting for Ozow BankGroupId.",
                    payout.Id);

                payout.Status = "AwaitingBankDetails";
                payout.UpdatedAt = DateTime.UtcNow;

                payout.Task.PayoutStatus =
                    "AwaitingBankDetails";

                payout.Task.UpdatedAt =
                    DateTime.UtcNow;

                await context.SaveChangesAsync(
                    cancellationToken);

                continue;
            }

            // Claim the payout atomically before calling Ozow.
            // Multiple API instances may run this hosted service at the
            // same time; only the instance that successfully changes the
            // row from Pending -> Processing may submit the payout.
            var now = DateTime.UtcNow;
            var claimed = await context.Payouts
                .Where(p =>
                    p.Id == payout.Id &&
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
            {
                // Another processor instance claimed this payout.
                continue;
            }

            // Refresh the task state after the atomic claim so this
            // processor does not rely on stale tracked values.
            await context.Entry(payout).ReloadAsync(cancellationToken);
            await context.Entry(payout.Task).ReloadAsync(cancellationToken);

            payout.Task.PayoutStatus = "Processing";
            payout.Task.UpdatedAt = now;
            await context.SaveChangesAsync(cancellationToken);

            var result =
                await payoutService.RequestPayoutAsync(
                    payout,
                    payout.BankAccount,
                    cancellationToken);

            if (result.Success)
            {
                payout.ProviderReference =
                    result.PayoutId;

                payout.Status =
                    "Processing";

                payout.NextAttemptAt = null;
                payout.ProcessingError = null;
                payout.UpdatedAt =
                    DateTime.UtcNow;

                payout.Task.PayoutStatus =
                    "Processing";

                payout.Task.PayoutReference =
                    result.PayoutId;

                payout.Task.UpdatedAt =
                    DateTime.UtcNow;

                await context.SaveChangesAsync(
                    cancellationToken);

                logger.LogInformation(
                    "Payout {PayoutId} submitted to Ozow.",
                    payout.Id);

                continue;
            }

            if (result.Retryable)
            {
                /*
                 * IMPORTANT:
                 *
                 * A retryable HTTP/network error is ambiguous.
                 *
                 * Ozow may have accepted the payout even though DFY did
                 * not receive the response. Therefore we MUST NOT move
                 * directly back to Pending, because that could submit the
                 * same merchant reference twice.
                 *
                 * Leave the payout in Processing. The stale-processing
                 * reconciliation at the top of this processor will query
                 * Ozow by MerchantReference after the 10-minute safety
                 * window:
                 *
                 *   Found    -> adopt Ozow payout ID; never resubmit.
                 *   NotFound -> explicitly safe to return to Pending.
                 *   Error    -> remain Processing; no duplicate submission.
                 */

                payout.Status =
                    "Processing";

                payout.ProcessingError =
                    result.Error ??
                    "Ozow payout request was ambiguous and requires reconciliation.";

                payout.NextAttemptAt =
                    DateTime.UtcNow.AddMinutes(10);

                payout.UpdatedAt =
                    DateTime.UtcNow;

                payout.Task.PayoutStatus =
                    "Processing";

                payout.Task.UpdatedAt =
                    DateTime.UtcNow;

                await context.SaveChangesAsync(
                    cancellationToken);

                logger.LogWarning(
                    "Ozow payout {PayoutId} received an ambiguous retryable response. " +
                    "Keeping payout Processing for reconciliation. No automatic resubmission.",
                    payout.Id);

                continue;
            }

            payout.Status = "Failed";
            payout.FailureReason =
                result.Error;

            payout.UpdatedAt =
                DateTime.UtcNow;

            payout.Task.PayoutStatus =
                "Failed";

            payout.Task.TaskStatus =
                "PayoutFailed";

            payout.Task.UpdatedAt =
                DateTime.UtcNow;

            await context.SaveChangesAsync(
                cancellationToken);

            logger.LogError(
                "Payout {PayoutId} failed: {Reason}",
                payout.Id,
                result.Error);
        }
    }
}
