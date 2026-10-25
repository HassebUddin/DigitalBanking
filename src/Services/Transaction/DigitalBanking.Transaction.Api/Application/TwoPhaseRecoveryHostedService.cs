namespace DigitalBanking.Transaction.Api.Application;

// Finishes two-phase commits the coordinator could not complete in the request: decisions some participant has
// not acknowledged yet (it was down), and transactions abandoned in phase 1 (presumed abort after 30 s).
public sealed class TwoPhaseRecoveryHostedService(IServiceScopeFactory scopeFactory, ILogger<TwoPhaseRecoveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var resolved = await scope.ServiceProvider.GetRequiredService<TwoPhaseCoordinator>().RecoverAsync(stoppingToken);
                if (resolved > 0)
                {
                    logger.LogInformation("Two-phase recovery pass handled {Count} transaction(s).", resolved);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Two-phase recovery pass failed: {Message}", exception.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }
}
