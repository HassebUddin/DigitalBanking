using DigitalBanking.Admin.Api.Ops.Compute;
using DigitalBanking.Admin.Api.Ops.Data;
using DigitalBanking.BuildingBlocks.Observability;

namespace DigitalBanking.Admin.Api.Ops;

public sealed record InsightAction(
    string Label,
    string Kind,
    string? RuleType = null,
    string? Value = null,
    int? Limit = null,
    string? Scenario = null,
    SimulationConfigRequest? Config = null,
    string? Command = null);

public sealed record Insight(string Id, string Severity, string Category, string Title, string Detail, IReadOnlyList<InsightAction> Actions);

/// <summary>Turns raw signals into ranked findings that tell the admin what is wrong and what to do.</summary>
public static class InsightsEngine
{
    public static IReadOnlyList<Insight> Evaluate(
        LiveSnapshot simulation,
        IReadOnlyList<ClientTraffic> clients,
        IReadOnlyList<EndpointTraffic> endpoints,
        IReadOnlyList<ServiceHealth> services,
        DatabaseSnapshot database,
        IReadOnlyList<TrafficRuleView> rules,
        IReadOnlyList<ClusterView> clusters,
        IReadOnlyList<ServicePodsView> pods)
    {
        var insights = new List<Insight>();
        var blockedClients = rules.Where(rule => rule.Type is TrafficRuleTypes.BlockClient or TrafficRuleTypes.RateLimitClient)
            .Select(rule => rule.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Local loopback traffic is every real user on this machine, never a single abuser.
        foreach (var client in clients.Where(client => client.Suspicious && !blockedClients.Contains(client.ClientId) && !TrafficRuleStore.IsLoopback(client.ClientId)))
        {
            insights.Add(new Insight(
                $"client:{client.ClientId}", "critical", "traffic",
                $"Abusive client {client.ClientId}",
                $"{client.RequestsPerSecond:0.#} req/s from {client.Region} ({client.SharePercent:0.#}% of all edge traffic), hitting {client.LastPath}.",
                [
                    new InsightAction("Block client", "rule", TrafficRuleTypes.BlockClient, client.ClientId),
                    new InsightAction("Rate limit to 5/s", "rule", TrafficRuleTypes.RateLimitClient, client.ClientId, 5)
                ]));
        }

        foreach (var service in services.Where(service => service.Status is "critical" or "degraded"))
        {
            insights.Add(new Insight(
                $"cpu:{service.Service}", service.Status == "critical" ? "critical" : "warning", "service",
                $"{service.Service} service CPU at {service.CpuPercent:0}%",
                $"{service.InFlightRequests} requests in flight, {service.MemoryMb:0} MB working set.",
                [
                    new InsightAction($"Throttle {service.Service} to 50/s", "rule", TrafficRuleTypes.ThrottleService, service.Service, 50),
                    new InsightAction("Stop CPU burn job", "config", Config: new SimulationConfigRequest { CpuBurn = false })
                ]));
        }

        foreach (var service in services.Where(service => service.Status == "down" && service.SecondsSinceHeartbeat >= 0))
        {
            insights.Add(new Insight($"down:{service.Service}", "critical", "service", $"{service.Service} service stopped reporting",
                $"No heartbeat for {service.SecondsSinceHeartbeat:0}s.", []));
        }

        foreach (var endpoint in endpoints.Where(endpoint => endpoint.RequestsPerSecond >= 2 && endpoint.ServerErrorRate >= 10))
        {
            insights.Add(new Insight($"errors:{endpoint.Endpoint}", "warning", "traffic", $"{endpoint.Endpoint} failing",
                $"{endpoint.ServerErrorRate:0.#}% server errors at {endpoint.RequestsPerSecond:0.#} req/s.",
                [new InsightAction("Disable endpoint", "rule", TrafficRuleTypes.BlockPath, endpoint.Endpoint.Split(' ', 2)[^1])]));
        }

        foreach (var endpoint in endpoints.Where(endpoint => endpoint.RequestsPerSecond >= 1 && endpoint.P95Ms >= 800))
        {
            insights.Add(new Insight($"slow:{endpoint.Endpoint}", "warning", "traffic", $"{endpoint.Endpoint} is slow",
                $"p95 latency {endpoint.P95Ms:0} ms on {endpoint.Target}.", []));
        }

        var writer = simulation.Writer;
        if (writer.QueueDepth > 5_000 || simulation.Settings.DbLatencyMs >= 700)
        {
            insights.Add(new Insight("db:backlog", writer.QueueDepth > 50_000 ? "critical" : "warning", "database",
                "Database cannot keep up with writes",
                $"{writer.QueueDepth:N0} transactions waiting, last batch took {writer.LastBatchMs:0} ms ({writer.Mode}).",
                [
                    new InsightAction("Remove injected latency", "config", Config: new SimulationConfigRequest { DbLatencyMs = 0 }),
                    new InsightAction("Halve TPS", "config", Config: new SimulationConfigRequest { BaseTps = Math.Max(1, simulation.Settings.BaseTps / 2) })
                ]));
        }

        if (simulation.Settings.DbStorm)
        {
            insights.Add(new Insight("db:storm", "critical", "database", "Row-by-row writes flooding SQL Server",
                $"{writer.CommandsPerSecond:0} commands/s from the transaction writer; batch requests at {database.BatchRequestsPerSecond:0}/s.",
                [new InsightAction("Switch back to bulk writes", "config", Config: new SimulationConfigRequest { DbStorm = false })]));
        }
        else if (database.Available && database.BatchRequestsPerSecond > 1_500)
        {
            insights.Add(new Insight("db:batches", "warning", "database", "High SQL batch rate",
                $"{database.BatchRequestsPerSecond:0} batch requests/s with {database.ActiveRequests} active requests.", []));
        }

        foreach (var province in simulation.Provinces.Where(province => province.Tps >= 1 && province.FailureRate >= 10 && !province.Suspended))
        {
            insights.Add(new Insight($"region:{province.Name}", "warning", "region", $"{province.Name}: {province.FailureRate:0.#}% transactions failing",
                $"{province.Tps:0.#} TPS in the last 10 seconds.",
                [new InsightAction($"Suspend {province.Name}", "rule", TrafficRuleTypes.BlockRegion, province.Name)]));
        }

        foreach (var province in simulation.Provinces.Where(province => province.SpikeLabel is not null))
        {
            insights.Add(new Insight($"spike:{province.Name}", "info", "region", $"{province.SpikeLabel} in {province.Name}",
                $"Traffic running at {province.Multiplier:0.#}x normal, {province.Tps:0.#} TPS.", []));
        }

        if (simulation.Settings.ErrorRatePercent > 0)
        {
            insights.Add(new Insight("sim:errors", "warning", "simulation", $"Core banking errors injected at {simulation.Settings.ErrorRatePercent:0}%",
                "Chaos setting is active.", [new InsightAction("Clear error injection", "config", Config: new SimulationConfigRequest { ErrorRatePercent = 0 })]));
        }

        foreach (var cluster in clusters)
        {
            var down = cluster.Nodes.FirstOrDefault(node => node.Status == "down");
            if (cluster.State == "down")
            {
                var replica = cluster.Nodes.FirstOrDefault(node => node.Role == "replica");
                insights.Add(new Insight($"db-down:{cluster.Province}", "critical", "database", $"{cluster.Province} has no writable primary",
                    "Regional transactions are failing until a node is promoted or restored.",
                    [
                        new InsightAction($"Promote {replica?.Name}", "db", Command: $"force-failover:{cluster.Province}"),
                        .. cluster.Nodes.Where(node => node.Status == "down").Select(node => new InsightAction($"Restore {node.Name}", "db", Command: $"restore:{node.Name}"))
                    ]));
            }
            else if (down is not null)
            {
                insights.Add(new Insight($"db-degraded:{down.Name}", "warning", "database", $"{cluster.Province} running without redundancy",
                    $"{down.Name} ({down.Zone}) is offline. Another failure would stop the region.",
                    [new InsightAction($"Restore {down.Name}", "db", Command: $"restore:{down.Name}")]));
            }

            if (cluster.CustomerLag + cluster.TransactionLag > 10_000)
            {
                insights.Add(new Insight($"db-lag:{cluster.Province}", "warning", "database", $"{cluster.Province} replica is {cluster.CustomerLag + cluster.TransactionLag:N0} rows behind",
                    "A failover now would lose these rows (RPO).",
                    cluster.ReplicationPaused ? [new InsightAction("Resume replication", "db", Command: $"resume-replication:{cluster.Province}")] : []));
            }
        }

        foreach (var service in pods.Where(service => service.Scalable))
        {
            var running = service.Pods.Count(pod => pod.State == "Running");
            var crashed = service.Pods.Where(pod => pod.State == "Crashed").ToList();
            if (service.Desired > 0 && running == 0)
            {
                insights.Add(new Insight($"pods-down:{service.Service}", "critical", "service", $"{service.Service}: no running pods",
                    "The gateway has nowhere to send this traffic (503).",
                    [new InsightAction("Scale to 2", "pod", Command: $"scale:{service.Service}:2")]));
            }
            else if (crashed.Count > 0)
            {
                insights.Add(new Insight($"pods-crashed:{service.Service}", "warning", "service", $"{service.Service}: {crashed.Count} pod{(crashed.Count == 1 ? "" : "s")} down, {running} serving",
                    service.AutoHeal ? "Self-healing will restart it within seconds." : "Self-healing is off; the pod stays down.",
                    service.AutoHeal ? [] : [new InsightAction("Enable self-healing", "pod", Command: $"autoheal:{service.Service}:on")]));
            }
            else if (running == 1 && service.Desired == 1)
            {
                var hot = services.FirstOrDefault(item => item.Service == service.Service && item.CpuPercent >= 50);
                if (hot is not null)
                {
                    insights.Add(new Insight($"pods-scale:{service.Service}", "warning", "service", $"{service.Service} is a single hot pod",
                        $"CPU {hot.CpuPercent:0}% on one replica. Scale out to spread the load.",
                        [new InsightAction("Scale to 3 pods", "pod", Command: $"scale:{service.Service}:3")]));
                }
            }
        }

        var order = new Dictionary<string, int> { ["critical"] = 0, ["warning"] = 1, ["info"] = 2 };
        return insights.OrderBy(insight => order[insight.Severity]).ToList();
    }
}
