using System.Text.Json;
using DigitalBanking.Contracts;

namespace DigitalBanking.BuildingBlocks.Outbox;

public static class OutboxWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void AddEvent<TEvent>(IOutboxDbContext dbContext, TEvent integrationEvent)
        where TEvent : IntegrationEvent
    {
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = integrationEvent.EventId,
            EventType = typeof(TEvent).Name,
            Payload = JsonSerializer.Serialize(integrationEvent, JsonOptions),
            OccurredAtUtc = integrationEvent.OccurredAtUtc
        });
    }
}
