using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalBanking.BuildingBlocks.Regions;

// Same as OutboxPublisherHostedService, but drains the outbox table of every province database.
public sealed class RegionalOutboxPublisherHostedService<TContext>(
    IServiceScopeFactory scopeFactory,
    ILogger<RegionalOutboxPublisherHostedService<TContext>> logger) : BackgroundService
    where TContext : DbContext, IOutboxDbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var region in BankRegions.All)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<RegionalDb<TContext>>().For(region);
                    var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

                    var unpublishedMessages = await dbContext.OutboxMessages
                        .Where(message => message.PublishedAtUtc == null)
                        .OrderBy(message => message.OccurredAtUtc)
                        .Take(50)
                        .ToListAsync(stoppingToken);

                    foreach (var message in unpublishedMessages)
                    {
                        await publisher.PublishAsync(message.EventType, message.Payload, stoppingToken);
                        message.PublishedAtUtc = DateTime.UtcNow;
                    }

                    if (unpublishedMessages.Count > 0)
                    {
                        await dbContext.SaveChangesAsync(stoppingToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One province being down (e.g. taken offline by the admin) must not stop the others.
                    logger.LogWarning(exception, "Outbox publishing for {Region} failed and will retry.", region);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
