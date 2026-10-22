using DigitalBanking.Admin.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Observability;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Admin.Api.Ops;

public sealed class CreateTrafficRuleRequest
{
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int? LimitPerSecond { get; set; }
    public string? Reason { get; set; }
    public int? ExpiresInMinutes { get; set; }
}

public sealed record TrafficRuleView(
    long Id, string Type, string Value, int? LimitPerSecond, string? Reason, string CreatedBy, DateTime CreatedAtUtc, DateTime? ExpiresAtUtc);

/// <summary>Admin-managed traffic rules. Persisted in SQL, served from memory to the gateway every 2 seconds.</summary>
public sealed class TrafficRuleStore(IServiceScopeFactory scopeFactory)
{
    private static readonly string[] AllowedTypes =
    [
        TrafficRuleTypes.BlockClient, TrafficRuleTypes.BlockRegion, TrafficRuleTypes.BlockPath,
        TrafficRuleTypes.RateLimitClient, TrafficRuleTypes.ThrottleService
    ];

    private volatile IReadOnlyList<TrafficRule> active = [];

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
        active = await dbContext.TrafficRules.AsNoTracking().Where(rule => rule.Active).ToListAsync(cancellationToken);
    }

    public IReadOnlyList<TrafficRuleView> List()
    {
        var now = DateTime.UtcNow;
        return active
            .Where(rule => rule.ExpiresAtUtc is null || rule.ExpiresAtUtc > now)
            .OrderByDescending(rule => rule.CreatedAtUtc)
            .Select(rule => new TrafficRuleView(rule.Id, rule.Type, rule.Value, rule.LimitPerSecond, rule.Reason, rule.CreatedBy, rule.CreatedAtUtc, rule.ExpiresAtUtc))
            .ToList();
    }

    public IReadOnlyList<TrafficRuleDto> ForGateway()
    {
        return List().Select(rule => new TrafficRuleDto
        {
            Id = rule.Id,
            Type = rule.Type,
            Value = rule.Value,
            LimitPerSecond = rule.LimitPerSecond,
            Reason = rule.Reason,
            ExpiresAtUtc = rule.ExpiresAtUtc
        }).ToList();
    }

    public bool IsRegionBlocked(string province)
    {
        var now = DateTime.UtcNow;
        return active.Any(rule =>
            rule.Type == TrafficRuleTypes.BlockRegion &&
            string.Equals(rule.Value, province, StringComparison.OrdinalIgnoreCase) &&
            (rule.ExpiresAtUtc is null || rule.ExpiresAtUtc > now));
    }

    public async Task<TrafficRuleView> AddAsync(CreateTrafficRuleRequest request, string createdBy, CancellationToken cancellationToken)
    {
        if (!AllowedTypes.Contains(request.Type))
        {
            throw new ValidationException($"Unknown rule type '{request.Type}'.");
        }

        if (string.IsNullOrWhiteSpace(request.Value))
        {
            throw new ValidationException("A rule needs a value such as a client id, region, path or service.");
        }

        var value = request.Value.Trim();
        if (request.Type is TrafficRuleTypes.BlockClient or TrafficRuleTypes.RateLimitClient && IsLoopback(value))
        {
            throw new ValidationException(
                $"{value} is this machine. Every browser and app running locally shares it, so blocking it would lock out all real users. Block a specific client id instead.");
        }

        if (request.Type == TrafficRuleTypes.BlockPath && (value == "/" || value.StartsWith("/api/ops", StringComparison.OrdinalIgnoreCase) || value.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException("Sign-in and the operations console cannot be disabled.");
        }

        var needsLimit = request.Type is TrafficRuleTypes.RateLimitClient or TrafficRuleTypes.ThrottleService;
        if (needsLimit && request.LimitPerSecond is null or < 1)
        {
            throw new ValidationException("Rate limits need a limit of at least 1 request per second.");
        }

        var rule = new TrafficRule
        {
            Type = request.Type,
            Value = value,
            LimitPerSecond = needsLimit ? request.LimitPerSecond : null,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            CreatedBy = createdBy,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = request.ExpiresInMinutes is > 0 ? DateTime.UtcNow.AddMinutes(request.ExpiresInMinutes.Value) : null
        };

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
        dbContext.TrafficRules.Add(rule);
        await dbContext.SaveChangesAsync(cancellationToken);
        active = [.. active, rule];
        return new TrafficRuleView(rule.Id, rule.Type, rule.Value, rule.LimitPerSecond, rule.Reason, rule.CreatedBy, rule.CreatedAtUtc, rule.ExpiresAtUtc);
    }

    public static bool IsLoopback(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        return text is "::1" or "127.0.0.1" or "localhost" or "::ffff:127.0.0.1" || text.StartsWith("127.", StringComparison.Ordinal);
    }

    public async Task<int> RemoveAllAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
        var now = DateTime.UtcNow;
        var removed = await dbContext.TrafficRules
            .Where(rule => rule.Active)
            .ExecuteUpdateAsync(setters => setters.SetProperty(rule => rule.Active, false).SetProperty(rule => rule.RemovedAtUtc, now), cancellationToken);
        active = [];
        return removed;
    }

    public async Task RemoveAsync(long id, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
        var rule = await dbContext.TrafficRules.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new NotFoundException("Rule was not found.");
        rule.Active = false;
        rule.RemovedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        active = active.Where(item => item.Id != id).ToList();
    }
}
