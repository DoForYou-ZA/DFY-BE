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
                var count = await escrowService.AutoReleaseExpiredEscrowsAsync();
                
                if (count > 0)
                    _logger.LogInformation("Released {Count} expired escrows", count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in escrow release service");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
