using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DigitalBanking.BuildingBlocks.Messaging;

public sealed class RabbitMqConnection : IDisposable
{
    private readonly ConnectionFactory _factory;
    private readonly object _sync = new();
    private IConnection? _connection;

    public RabbitMqConnection(IOptions<RabbitMqOptions> options)
    {
        var settings = options.Value;
        _factory = new ConnectionFactory
        {
            HostName = settings.HostName,
            Port = settings.Port,
            UserName = settings.UserName,
            Password = settings.Password,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true
        };
    }

    public IConnection GetConnection()
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        lock (_sync)
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            _connection?.Dispose();
            _connection = _factory.CreateConnection();
            return _connection;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
