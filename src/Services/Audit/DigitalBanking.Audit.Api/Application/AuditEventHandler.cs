using System.Text.Json;
using DigitalBanking.Audit.Api.Domain;
using DigitalBanking.Audit.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Inbox;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts;

namespace DigitalBanking.Audit.Api.Application;

public sealed class AuditEventHandler<TEvent>(RegionalDb<AuditDbContext> db) : IIntegrationEventHandler
    where TEvent : IntegrationEvent
{
    public async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        var integrationEvent = JsonSerializer.Deserialize<TEvent>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (integrationEvent is null)
        {
            return;
        }

        // The audit entry goes to the province of the customer who did it (staff and system events: head office).
        var actorUserId = FindUserId(payload);
        var dbContext = actorUserId is { } userId ? await db.ForUserRegionAsync(userId, cancellationToken) : db.For(BankRegions.Default);
        if (!await InboxGuard.TryBeginAsync(dbContext, integrationEvent.EventId, typeof(TEvent).Name, cancellationToken))
        {
            return;
        }

        dbContext.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            EventType = typeof(TEvent).Name,
            ActorUserId = actorUserId,
            Payload = payload,
            OccurredAtUtc = integrationEvent.OccurredAtUtc
        });

        InboxGuard.MarkProcessed(dbContext, integrationEvent.EventId);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static Guid? FindUserId(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        foreach (var propertyName in new[] { "userId", "sourceUserId", "actorUserId" })
        {
            if (document.RootElement.TryGetProperty(propertyName, out var property) &&
                Guid.TryParse(property.GetString(), out var userId))
            {
                return userId;
            }
        }

        return null;
    }
}
