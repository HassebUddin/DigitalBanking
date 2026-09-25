using DigitalBanking.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalBanking.BuildingBlocks.Outbox;

public sealed class OutboxPublisherHostedService<TDbContext>(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxPublisherHostedService<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext, IOutboxDbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
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
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Outbox publishing failed and will retry.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
