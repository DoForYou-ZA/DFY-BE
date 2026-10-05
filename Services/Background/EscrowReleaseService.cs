using Microsoft.EntityFrameworkCore;
using DoForYou.API.Services;

namespace DoForYou.API.Services.Background;

public class EscrowReleaseService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EscrowReleaseService> _logger;

    public EscrowReleaseService(IServiceProvider serviceProvider, ILogger<EscrowReleaseService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Escrow Release Service started");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var escrowService = scope.ServiceProvider.GetRequiredService<IEscrowService>();
                var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
                var db = scope.ServiceProvider.GetRequiredService<DoForYou.API.Data.AppDbContext>();
                var expiredTasks = await db.Tasks
                    .Where(t => t.TaskStatus == "AwaitingConfirmation" &&
                                t.EscrowStatus == "held" &&
                                t.PaymentStatus == "EscrowHeld" &&
                                t.EscrowHoldUntil.HasValue &&
                                t.EscrowHoldUntil <= DateTime.UtcNow)
                    .ToListAsync(stoppingToken);

                var count = 0;
                foreach (var task in expiredTasks)
                {
                    if (!await escrowService.ReleaseEscrowAsync(task.Id))
                        continue;

                    count++;
                    if (task.AcceptedByUserId.HasValue)
                    {
                        await notificationService.NotifyPaymentReleasedAsync(
                            task.AcceptedByUserId.Value,
                            task.TaskDescription,
                            task.PayoutAmount);
                    }

                    await notificationService.CreateNotificationAsync(
                        task.CreatedByUserId,
                        "task_auto_confirmed",
                        "Task Automatically Confirmed",
                        $"No confirmation or dispute was received within 24 hours for task {task.TaskId}. The task was automatically confirmed and the runner payout was initiated.",
                        task.Id);
                }

                if (count > 0)
                    _logger.LogInformation("Automatically confirmed and released {Count} expired creator confirmations", count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in escrow release service");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
