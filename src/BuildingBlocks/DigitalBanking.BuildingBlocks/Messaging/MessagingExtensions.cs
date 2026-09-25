using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBanking.BuildingBlocks.Messaging;

public static class MessagingExtensions
{
    public static IServiceCollection AddRabbitMq(this IServiceCollection services, IConfiguration configuration, string queueName)
    {
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.PostConfigure<RabbitMqOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.QueueName))
            {
                options.QueueName = queueName;
            }
        });
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        services.AddHostedService<RabbitMqEventConsumer>();
        return services;
    }

    public static IServiceCollection AddEventHandler<THandler>(this IServiceCollection services, string eventType)
        where THandler : class, IIntegrationEventHandler
    {
        services.AddScoped<THandler>();
        services.AddSingleton(serviceProvider => new EventSubscription
        {
            EventType = eventType,
            Handler = async (payload, cancellationToken) =>
            {
                using var scope = serviceProvider.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                await handler.HandleAsync(payload, cancellationToken);
            }
        });
        return services;
    }
}

public interface IIntegrationEventHandler
{
    Task HandleAsync(string payload, CancellationToken cancellationToken);
}
