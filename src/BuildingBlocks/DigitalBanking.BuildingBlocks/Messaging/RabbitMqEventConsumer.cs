using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DigitalBanking.BuildingBlocks.Messaging;

public sealed class RabbitMqEventConsumer(
    RabbitMqConnection connection,
    IOptions<RabbitMqOptions> options,
    IEnumerable<EventSubscription> subscriptions,
    ILogger<RabbitMqEventConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                StartConsumer(stoppingToken);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "RabbitMQ consumer will retry.");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private void StartConsumer(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var channel = connection.GetConnection().CreateModel();
        channel.ExchangeDeclare(settings.ExchangeName, ExchangeType.Topic, durable: true);
        channel.QueueDeclare(settings.QueueName, durable: true, exclusive: false, autoDelete: false);
        channel.BasicQos(0, 10, false);

        foreach (var subscription in subscriptions)
        {
            channel.QueueBind(settings.QueueName, settings.ExchangeName, subscription.EventType);
        }

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, arguments) =>
        {
            var eventType = arguments.RoutingKey;
            var payload = Encoding.UTF8.GetString(arguments.Body.ToArray());
            var subscription = subscriptions.FirstOrDefault(item => item.EventType == eventType);

            if (subscription is null)
            {
                channel.BasicAck(arguments.DeliveryTag, false);
                return;
            }

            try
            {
                await subscription.Handler(payload, stoppingToken);
                channel.BasicAck(arguments.DeliveryTag, false);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to process event {EventType}", eventType);
                channel.BasicNack(arguments.DeliveryTag, false, true);
            }
        };

        channel.BasicConsume(settings.QueueName, autoAck: false, consumer);
    }
}
