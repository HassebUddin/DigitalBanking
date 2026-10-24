using System.Security.Claims;
using DigitalBanking.Admin.Api.Ops;
using DigitalBanking.Admin.Api.Ops.Compute;
using DigitalBanking.Admin.Api.Ops.Data;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Observability;
using DigitalBanking.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Admin.Api.Controllers;

public sealed class ClusterSettingsRequest
{
    public bool? AutoFailover { get; set; }
    public bool? ReplicationPaused { get; set; }
}

public sealed class ScaleRequest
{
    public int Replicas { get; set; }
}

public sealed class ToggleRequest
{
    public bool Enabled { get; set; }
}

public sealed class CommandRequest
{
    public string Command { get; set; } = string.Empty;
}

public sealed class QueryRequest
{
    public string Sql { get; set; } = string.Empty;
}

public sealed class ResetRequest
{
    public bool Rules { get; set; } = true;
    public bool Chaos { get; set; } = true;
    public bool Pods { get; set; }
    public bool Databases { get; set; }
    public bool StopSimulation { get; set; }
}

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/ops")]
public sealed class OpsController(
    SimulationEngine engine,
    LoadGenerator loadGenerator,
    LogStore logStore,
    LogQueryService logQuery,
    ServiceHealthStore health,
    HostMetrics host,
    DatabaseMonitor database,
    TrafficRuleStore rules,
    RegionalDataPlane dataPlane,
    PodSupervisor supervisor,
    OpsEventLog eventLog,
    DataExplorer explorer,
    CustomerBatchJobs batches,
    ICustomerSearch customerSearch) : ControllerBase
{
    [HttpGet("overview")]
    public IActionResult Overview()
    {
        var simulation = engine.Snapshot();
        var clients = logStore.TopClients(15, 10);
        var endpoints = logStore.TopEndpoints(30, 10);
        var serviceHealth = health.Snapshot();
        var traffic = logStore.ServiceTraffic(10, 60).ToDictionary(item => item.Service, StringComparer.OrdinalIgnoreCase);
        var databaseSnapshot = database.Latest;
        var activeRules = rules.List();
        var clusters = dataPlane.Snapshot();
        var services = supervisor.Snapshot();

        return Ok(new
        {
            simulation,
            edge = logStore.EdgeSeries(120),
            services = serviceHealth.Select(service => new
            {
                health = service,
                traffic = traffic.GetValueOrDefault(service.Service)
            }),
            pods = PodsView(services),
            clusters,
            synchronousCommit = dataPlane.SynchronousCommit,
            clients,
            endpoints,
            database = databaseSnapshot,
            host = new { cpuPercent = host.CpuPercent(), cores = Environment.ProcessorCount, machine = Environment.MachineName },
            loadGenerator = loadGenerator.Stats(),
            rules = activeRules,
            insights = InsightsEngine.Evaluate(simulation, clients, endpoints, serviceHealth, databaseSnapshot, activeRules, clusters, services),
            events = eventLog.Recent(40),
            jobs = batches.Active(),
            customers = clusters.Sum(cluster => cluster.Customers),
            logsIngested = logStore.Ingested,
            logsPending = logStore.PendingWrites,
            serverTimeUtc = DateTime.UtcNow
        });
    }

    /// <summary>Pods joined with their own heartbeat and with how the gateway's load balancer sees them.</summary>
    private object PodsView(IReadOnlyList<ServicePodsView> services)
    {
        var instances = health.Instances().ToDictionary(item => item.Instance, StringComparer.OrdinalIgnoreCase);
        var balancer = health.GatewayDestinations.ToDictionary(item => item.Address, StringComparer.OrdinalIgnoreCase);
        return services.Select(service => new
        {
            service.Service,
            service.Desired,
            service.AutoHeal,
            service.Scalable,
            pods = service.Pods.Select(pod =>
            {
                instances.TryGetValue(pod.Address, out var metrics);
                balancer.TryGetValue(pod.Address, out var lb);
                return new
                {
                    pod.Id,
                    pod.Port,
                    pod.Address,
                    pod.State,
                    pod.Managed,
                    pod.Drained,
                    pod.Restarts,
                    pod.ProcessId,
                    pod.UptimeSeconds,
                    pod.LastReason,
                    cpuPercent = metrics?.CpuPercent ?? 0,
                    memoryMb = metrics?.MemoryMb ?? 0,
                    inFlight = metrics?.InFlightRequests ?? 0,
                    cpuSeries = metrics?.CpuSeries ?? [],
                    balancer = lb is null ? (service.Scalable ? "not routed" : "direct") : lb.Active == "Unhealthy" || lb.Passive == "Unhealthy" ? "unhealthy" : lb.Active == "Healthy" ? "healthy" : "probing",
                    balancerInFlight = lb?.InFlight ?? 0
                };
            })
        });
    }

    [HttpPut("simulation")]
    public IActionResult Configure(SimulationConfigRequest request) => Ok(engine.Apply(request));

    [HttpPost("simulation/spike")]
    public IActionResult Spike(SpikeRequest request)
    {
        engine.AddSpike(request);
        eventLog.Info("simulation", request.Province, $"{request.Province} surge {request.Multiplier}x for {request.Seconds}s.");
        return Ok(engine.Settings());
    }

    [HttpPost("simulation/scenario/{name}")]
    public IActionResult Scenario(string name)
    {
        engine.ApplyScenario(name);
        eventLog.Info("simulation", "scenario", name == "reset" ? "All chaos cleared." : $"Scenario '{name}' started.");
        return Ok(engine.Settings());
    }

    /// <summary>One switch to undo everything an admin (or a drill) changed.</summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(ResetRequest request, CancellationToken cancellationToken)
    {
        var done = new List<string>();
        if (request.Rules)
        {
            var removed = await rules.RemoveAllAsync(cancellationToken);
            done.Add($"{removed} rule{(removed == 1 ? string.Empty : "s")} removed");
        }

        if (request.Chaos)
        {
            engine.ApplyScenario("reset");
            done.Add("chaos cleared");
        }

        if (request.Pods)
        {
            supervisor.ResetAll();
            done.Add("pods back to one per service");
        }

        if (request.Databases)
        {
            await dataPlane.ResetAsync(cancellationToken);
            done.Add("all database nodes online");
        }

        if (request.StopSimulation)
        {
            engine.Apply(new SimulationConfigRequest { Running = false });
            done.Add("simulation paused");
        }

        var summary = done.Count == 0 ? "Nothing selected." : string.Join(", ", done) + ".";
        eventLog.Success("system", "reset", $"Reset by admin: {summary}");
        return Ok(new { summary });
    }

    // ---- Database clusters --------------------------------------------------------------------

    [HttpPost("db/nodes/{node}/offline")]
    public async Task<IActionResult> TakeOffline(string node, CancellationToken cancellationToken)
    {
        await dataPlane.TakeOfflineAsync(node, cancellationToken);
        return NoContent();
    }

    [HttpPost("db/nodes/{node}/online")]
    public async Task<IActionResult> BringOnline(string node, CancellationToken cancellationToken)
    {
        await dataPlane.BringOnlineAsync(node, cancellationToken);
        return NoContent();
    }

    [HttpPost("db/nodes/{node}/maintenance")]
    public async Task<IActionResult> Maintenance(string node, ToggleRequest request, CancellationToken cancellationToken)
    {
        await dataPlane.SetMaintenanceAsync(node, request.Enabled, cancellationToken);
        return NoContent();
    }

    [HttpPut("db/commit-mode")]
    public async Task<IActionResult> CommitMode(ToggleRequest request, CancellationToken cancellationToken)
    {
        await dataPlane.SetSynchronousCommitAsync(request.Enabled, cancellationToken);
        return Ok(new { synchronousCommit = request.Enabled });
    }

    // Network partition drill between the coordinator and one node (services can still reach it).
    [HttpPost("db/nodes/{node}/partition")]
    public IActionResult Partition(string node, ToggleRequest request)
    {
        dataPlane.SetPartition(node, request.Enabled);
        return NoContent();
    }

    [HttpPost("db/clusters/{province}/switchover")]
    public async Task<IActionResult> Switchover(string province, CancellationToken cancellationToken)
    {
        await dataPlane.SwitchoverAsync(province, "requested by admin", cancellationToken);
        return NoContent();
    }

    [HttpPost("db/clusters/{province}/failover")]
    public async Task<IActionResult> Failover(string province, CancellationToken cancellationToken)
    {
        await dataPlane.ForceFailoverAsync(province, cancellationToken);
        return NoContent();
    }

    [HttpPut("db/clusters/{province}")]
    public async Task<IActionResult> ConfigureCluster(string province, ClusterSettingsRequest request, CancellationToken cancellationToken)
    {
        await dataPlane.ConfigureAsync(province, request.AutoFailover, request.ReplicationPaused, cancellationToken);
        return NoContent();
    }

    // ---- Pods -----------------------------------------------------------------------------------

    [HttpPost("pods/{id}/crash")]
    public IActionResult Crash(string id)
    {
        supervisor.Crash(id);
        return NoContent();
    }

    [HttpPost("pods/{id}/restart")]
    public IActionResult Restart(string id)
    {
        supervisor.Restart(id);
        return NoContent();
    }

    [HttpPost("pods/{id}/drain")]
    public IActionResult Drain(string id, ToggleRequest request)
    {
        supervisor.SetDrained(id, request.Enabled);
        return NoContent();
    }

    [HttpPut("services/{service}/scale")]
    public IActionResult Scale(string service, ScaleRequest request)
    {
        supervisor.Scale(service, request.Replicas);
        return NoContent();
    }

    [HttpPut("services/{service}/autoheal")]
    public IActionResult AutoHeal(string service, ToggleRequest request)
    {
        supervisor.SetAutoHeal(service, request.Enabled);
        return NoContent();
    }

    /// <summary>Runs the one-click actions attached to insights ("scale:account:3", "restore:punjab-a", …).</summary>
    [HttpPost("commands")]
    public async Task<IActionResult> Command(CommandRequest request, CancellationToken cancellationToken)
    {
        var parts = request.Command.Split(':');
        switch (parts)
        {
            case ["force-failover", var province]:
                await dataPlane.ForceFailoverAsync(province, cancellationToken);
                break;
            case ["restore", var node]:
                await dataPlane.BringOnlineAsync(node, cancellationToken);
                break;
            case ["resume-replication", var province]:
                await dataPlane.ConfigureAsync(province, null, false, cancellationToken);
                break;
            case ["scale", var service, var replicas] when int.TryParse(replicas, out var count):
                supervisor.Scale(service, count);
                break;
            case ["autoheal", var service, var state]:
                supervisor.SetAutoHeal(service, state == "on");
                break;
            default:
                throw new ValidationException($"Unknown command '{request.Command}'.");
        }

        return NoContent();
    }

    // ---- Data explorer -------------------------------------------------------------------------

    [HttpGet("data/databases")]
    public async Task<IActionResult> Databases(CancellationToken cancellationToken) => Ok(await explorer.DatabasesAsync(cancellationToken));

    [HttpGet("data/{key}/tables/{table}")]
    public async Task<IActionResult> Table(
        string key, string table, int page = 0, int size = 50, string? sort = null, bool desc = true, string? column = null, string? value = null,
        CancellationToken cancellationToken = default)
    {
        return Ok(await explorer.TableAsync(key, table, page, size, sort, desc, column, value, cancellationToken));
    }

    [HttpPost("data/{key}/query")]
    public async Task<IActionResult> Query(string key, QueryRequest request, CancellationToken cancellationToken)
    {
        return Ok(await explorer.QueryAsync(key, request.Sql, cancellationToken));
    }

    // ---- Logs, rules, customers ---------------------------------------------------------------

    [HttpGet("logs")]
    public async Task<IActionResult> Logs([FromQuery] LogQuery query, CancellationToken cancellationToken)
    {
        return Ok(await logQuery.SearchAsync(query, cancellationToken));
    }

    [HttpGet("rules")]
    public IActionResult Rules() => Ok(rules.List());

    [HttpPost("rules")]
    public async Task<IActionResult> AddRule(CreateTrafficRuleRequest request, CancellationToken cancellationToken)
    {
        var createdBy = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email") ?? "admin";
        var rule = await rules.AddAsync(request, createdBy, cancellationToken);
        eventLog.Warning("rule", rule.Value, $"Rule added: {rule.Type} {rule.Value}{(rule.LimitPerSecond is { } limit ? $" ({limit}/s)" : string.Empty)}.");
        return Ok(rule);
    }

    [HttpDelete("rules/{id:long}")]
    public async Task<IActionResult> RemoveRule(long id, CancellationToken cancellationToken)
    {
        await rules.RemoveAsync(id, cancellationToken);
        eventLog.Info("rule", $"rule-{id}", $"Rule #{id} removed.");
        return NoContent();
    }

    [HttpGet("regions")]
    public IActionResult Regions()
    {
        return Ok(PakistanGeo.Provinces.Select(province =>
        {
            var cluster = dataPlane.Cluster(province.Name);
            return new
            {
                province.Name,
                province.Code,
                province.PopulationMillions,
                customers = dataPlane.CustomerCount(province.Name),
                cities = province.Cities.Select(city => new { city.Name, city.Lat, city.Lng, customers = cluster.CityCounts.GetValueOrDefault(city.Name) })
            };
        }));
    }

    [HttpGet("customers/batches")]
    public async Task<IActionResult> Batches(CancellationToken cancellationToken) => Ok(await batches.ListAsync(cancellationToken));

    [HttpPost("customers/batches")]
    public async Task<IActionResult> StartBatch(StartCustomerBatchRequest request, CancellationToken cancellationToken)
    {
        return Ok(await batches.EnqueueAsync(request, cancellationToken));
    }

    [HttpPost("customers/batches/{id:int}/cancel")]
    public IActionResult CancelBatch(int id) => batches.Cancel(id) ? NoContent() : NotFound();

    [HttpGet("customers/search")]
    public async Task<IActionResult> SearchCustomers(string? q, string? province, string? city, int take = 50, int page = 0, CancellationToken cancellationToken = default)
    {
        return Ok(await customerSearch.SearchAsync(q, province, city, take, page, cancellationToken));
    }
}

/// <summary>Service-to-service endpoints used by the telemetry shipper in every service and by the gateway.</summary>
[ApiController]
[AllowAnonymous]
[InternalApiKey]
[Route("api/ops/internal")]
public sealed class OpsInternalController(LogStore logStore, ServiceHealthStore health, TrafficRuleStore rules, PodSupervisor supervisor) : ControllerBase
{
    [HttpPost("logs")]
    public IActionResult Logs(List<RequestLogEntry> entries)
    {
        logStore.Ingest(entries);
        return Accepted();
    }

    [HttpPost("heartbeat")]
    public IActionResult Heartbeat(ServiceHeartbeat heartbeat)
    {
        health.Record(heartbeat);
        return Accepted();
    }

    [HttpGet("rules")]
    public IActionResult Rules() => Ok(rules.ForGateway());

    [HttpGet("topology")]
    public IActionResult Topology() => Ok(supervisor.Topology());
}
