using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace DigitalBanking.BuildingBlocks.Observability;

public sealed class RequestTelemetryMiddleware(
    RequestDelegate next,
    TelemetryBuffer buffer,
    TrafficRuleCache ruleCache,
    TelemetryOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        // Load balancer health probes carry no client id; logging them would only add noise.
        var healthProbe = path == "/probe" && !context.Request.Headers.ContainsKey(TelemetryHeaders.ClientId);
        if (IsIgnored(path) || healthProbe || HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var clientId = ResolveClientId(context);
        var region = context.Request.Headers[TelemetryHeaders.Region].FirstOrDefault() ?? "Unknown";
        var target = options.TargetResolver?.Invoke(context);
        var entry = new RequestLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Service = options.ServiceName,
            Target = target,
            Method = context.Request.Method,
            Path = path,
            Query = context.Request.QueryString.HasValue ? Truncate(context.Request.QueryString.Value, 300) : null,
            ClientId = clientId,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            Region = region,
            UserAgent = Truncate(context.Request.Headers.UserAgent.ToString(), 200),
            TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            Source = context.Request.Headers[TelemetryHeaders.Source].FirstOrDefault() ?? "real"
        };

        buffer.BeginRequest();
        try
        {
            if (options.EnforceTrafficRules && !IsControlPlane(path))
            {
                var decision = ruleCache.Evaluate(clientId, region, path, target);
                if (!decision.Allowed)
                {
                    entry.Blocked = true;
                    entry.BlockReason = decision.Reason;
                    context.Response.StatusCode = decision.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        code = decision.StatusCode == 429 ? "rate_limited" : "blocked",
                        message = decision.Reason
                    }));
                    return;
                }
            }

            await next(context);
        }
        finally
        {
            entry.StatusCode = context.Response.StatusCode;
            entry.Instance = options.InstanceResolver?.Invoke(context) ?? options.SelfInstance;
            entry.DurationMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2);
            entry.UserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
            buffer.EndRequest(entry);
        }
    }

    /// <summary>
    /// Sign-in and the operations console are never subject to traffic rules, so an admin can always
    /// get back in and undo a rule that blocked too much.
    /// </summary>
    private static bool IsControlPlane(string path)
    {
        return path.StartsWith("/api/ops", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnored(string path)
    {
        return path.StartsWith("/api/ops/internal", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveClientId(HttpContext context)
    {
        var explicitClient = context.Request.Headers[TelemetryHeaders.ClientId].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(explicitClient))
        {
            return Truncate(explicitClient, 80)!;
        }

        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        return !string.IsNullOrWhiteSpace(forwarded)
            ? forwarded
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static string? Truncate(string? value, int length)
    {
        return value is { Length: > 0 } && value.Length > length ? value[..length] : value;
    }
}
