using DigitalBanking.Contracts;

namespace DigitalBanking.BuildingBlocks.Messaging;

public interface IEventPublisher
{
    Task PublishAsync(string eventType, string payload, CancellationToken cancellationToken = default);
    Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : IntegrationEvent;
}
