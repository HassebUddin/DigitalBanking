namespace DigitalBanking.BuildingBlocks.Observability;

public sealed class RequestLogEntry
{
    public DateTime TimestampUtc { get; set; }
    public string Service { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? Instance { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Query { get; set; }
    public int StatusCode { get; set; }
    public double DurationMs { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string Region { get; set; } = "Unknown";
    public string? UserId { get; set; }
    public string? UserAgent { get; set; }
    public bool Blocked { get; set; }
    public string? BlockReason { get; set; }
    public string? TraceId { get; set; }
    public string Source { get; set; } = "real";
}

public sealed class ServiceHeartbeat
{
    public string Service { get; set; } = string.Empty;
    public string Instance { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public List<DestinationHealthDto>? Destinations { get; set; }
    public DateTime TimestampUtc { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
    public double GcHeapMb { get; set; }
    public int Threads { get; set; }
    public int InFlightRequests { get; set; }
    public long TotalRequests { get; set; }
    public DateTime StartedAtUtc { get; set; }
}

/// <summary>How the gateway's load balancer currently sees one backend pod.</summary>
public sealed class DestinationHealthDto
{
    public string Cluster { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Active { get; set; } = "Unknown";
    public string Passive { get; set; } = "Unknown";
    public int InFlight { get; set; }
}

public sealed class TrafficRuleDto
{
    public long Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int? LimitPerSecond { get; set; }
    public string? Reason { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

public static class TrafficRuleTypes
{
    public const string BlockClient = "block-client";
    public const string BlockRegion = "block-region";
    public const string BlockPath = "block-path";
    public const string RateLimitClient = "rate-limit-client";
    public const string ThrottleService = "throttle-service";
}

public static class TelemetryHeaders
{
    public const string ClientId = "X-Client-Id";
    public const string Region = "X-Region";
    public const string Source = "X-Traffic-Source";
}
