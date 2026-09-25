using System.Text;
using System.Text.Json;
using DigitalBanking.Contracts;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DigitalBanking.BuildingBlocks.Messaging;

public sealed class RabbitMqEventPublisher(RabbitMqConnection connection, IOptions<RabbitMqOptions> options) : IEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task PublishAsync(string eventType, string payload, CancellationToken cancellationToken = default)
    {
        using var channel = connection.GetConnection().CreateModel();
        channel.ExchangeDeclare(options.Value.ExchangeName, ExchangeType.Topic, durable: true);
        var body = Encoding.UTF8.GetBytes(payload);
        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = "application/json";
        properties.Type = eventType;
        channel.BasicPublish(options.Value.ExchangeName, eventType, properties, body);
        return Task.CompletedTask;
    }

    public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : IntegrationEvent
    {
        var payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), JsonOptions);
        return PublishAsync(typeof(TEvent).Name, payload, cancellationToken);
    }
}
