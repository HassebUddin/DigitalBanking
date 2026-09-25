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

public sealed class RedisCacheService(IConnectionMultiplexer multiplexer) : ICacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var value = await multiplexer.GetDatabase().StringGetAsync(key);
        if (value.IsNullOrEmpty)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(value!, JsonOptions);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(value, JsonOptions);
        await multiplexer.GetDatabase().StringSetAsync(key, payload, timeToLive);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        return multiplexer.GetDatabase().KeyDeleteAsync(key);
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
