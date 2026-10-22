using System.Collections.Concurrent;

namespace DigitalBanking.BuildingBlocks.Observability;

public sealed record TrafficDecision(bool Allowed, int StatusCode, string? Reason)
{
    public static readonly TrafficDecision Allow = new(true, 200, null);
}

/// <summary>Holds the admin-defined traffic rules and enforces them with per-second counters.</summary>
public sealed class TrafficRuleCache
{
    private volatile IReadOnlyList<TrafficRuleDto> rules = [];
    private readonly ConcurrentDictionary<string, WindowCounter> counters = new();

    public IReadOnlyList<TrafficRuleDto> Rules => rules;

    public void Replace(IReadOnlyList<TrafficRuleDto> nextRules)
    {
        rules = nextRules;
        if (counters.Count > 10_000)
        {
            counters.Clear();
        }
    }

    public TrafficDecision Evaluate(string clientId, string region, string path, string? target)
    {
        var now = DateTime.UtcNow;
        foreach (var rule in rules)
        {
            if (rule.ExpiresAtUtc is { } expires && expires < now)
            {
                continue;
            }

            switch (rule.Type)
            {
                case TrafficRuleTypes.BlockClient when Same(rule.Value, clientId):
                    return new TrafficDecision(false, 403, rule.Reason ?? $"Client {clientId} is blocked");
                case TrafficRuleTypes.BlockRegion when Same(rule.Value, region):
                    return new TrafficDecision(false, 403, rule.Reason ?? $"Traffic from {region} is suspended");
                case TrafficRuleTypes.BlockPath when path.StartsWith(rule.Value, StringComparison.OrdinalIgnoreCase):
                    return new TrafficDecision(false, 403, rule.Reason ?? $"Endpoint {rule.Value} is disabled");
                case TrafficRuleTypes.RateLimitClient when Same(rule.Value, clientId) && Exceeded($"c:{rule.Id}:{clientId}", rule.LimitPerSecond):
                    return new TrafficDecision(false, 429, rule.Reason ?? $"Client {clientId} exceeded {rule.LimitPerSecond} req/s");
                case TrafficRuleTypes.ThrottleService when target is not null && Same(rule.Value, target) && Exceeded($"s:{rule.Id}", rule.LimitPerSecond):
                    return new TrafficDecision(false, 429, rule.Reason ?? $"{target} is throttled to {rule.LimitPerSecond} req/s");
            }
        }

        return TrafficDecision.Allow;
    }

    private bool Exceeded(string key, int? limit)
    {
        var second = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        var counter = counters.GetOrAdd(key, _ => new WindowCounter());
        return counter.Hit(second) > (limit ?? 10);
    }

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed class WindowCounter
    {
        private long window;
        private int count;

        public int Hit(long second)
        {
            lock (this)
            {
                if (window != second)
                {
                    window = second;
                    count = 0;
                }

                return ++count;
            }
        }
    }
}
