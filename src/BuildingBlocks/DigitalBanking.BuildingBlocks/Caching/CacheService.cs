using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace DigitalBanking.BuildingBlocks.Caching;

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetAsync<T>(string key, T value, TimeSpan timeToLive, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

// Best effort: the database is the source of truth, so when Redis is down a read is a miss and a write is
// skipped (without waiting for a connect timeout) instead of failing the request that already did its work.
public sealed class RedisCacheService(IConnectionMultiplexer multiplexer) : ICacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (!multiplexer.IsConnected)
        {
            return default;
        }

        try
        {
            var value = await multiplexer.GetDatabase().StringGetAsync(key);
            return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>(value!, JsonOptions);
        }
        catch (RedisException)
        {
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        if (!multiplexer.IsConnected)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(value, JsonOptions);
            await multiplexer.GetDatabase().StringSetAsync(key, payload, timeToLive);
        }
        catch (RedisException)
        {
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!multiplexer.IsConnected)
        {
            return;
        }

        try
        {
            await multiplexer.GetDatabase().KeyDeleteAsync(key);
        }
        catch (RedisException)
        {
        }
    }
}

public static class CachingExtensions
{
    public static IServiceCollection AddRedisCache(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Redis") ?? "localhost:6379";
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect($"{connectionString},abortConnect=false"));
        services.AddSingleton<ICacheService, RedisCacheService>();
        return services;
    }
}
