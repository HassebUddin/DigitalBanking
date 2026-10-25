using System.Diagnostics;
using System.Net.NetworkInformation;
using DigitalBanking.BuildingBlocks.Exceptions;

namespace DigitalBanking.Admin.Api.Ops.Compute;

public sealed record ServiceSpec(string Name, string Project, int BasePort);

public sealed class Pod
{
    public required string Id { get; init; }
    public required string Service { get; init; }
    public required int Port { get; init; }
    public required int Slot { get; init; }
    public string State { get; set; } = "Starting";
    public bool Managed { get; set; }
    public bool Drained { get; set; }
    public int Restarts { get; set; }
    public int? ProcessId { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CrashedAtUtc { get; set; }
    public string? LastReason { get; set; }
    public Process? Process { get; set; }
    public string Address => $"localhost:{Port}";
}

public sealed record PodView(
    string Id, string Service, int Port, string Address, string State, bool Managed, bool Drained, int Restarts, int? ProcessId,
    double UptimeSeconds, string? LastReason);

public sealed record ServicePodsView(string Service, int Desired, bool AutoHeal, bool Scalable, IReadOnlyList<PodView> Pods);

/// <summary>
/// A small, Kubernetes-style replica controller for the stateless services. It discovers running
/// instances, keeps each service at its desired replica count (restarting crashed pods), and
/// publishes the live pod list that the gateway load-balances across.
/// </summary>
public sealed class PodSupervisor(IHostEnvironment environment, OpsEventLog events, Consensus.CoordinatorLeadership leadership, ILogger<PodSupervisor> logger) : BackgroundService
{
    public const int MaxReplicas = 5;

    private static readonly ServiceSpec[] Specs =
    [
        new("identity", "Identity", 5101),
        new("customer", "Customer", 5102),
        new("account", "Account", 5103),
        new("transaction", "Transaction", 5104),
        new("notification", "Notification", 5105),
        new("audit", "Audit", 5106)
    ];

    // Stateful or control-plane processes are shown but never scaled.
    private static readonly (string Name, int Port)[] Fixed = [("gateway", 5000), ("admin", 5107), ("chat", 5108)];

    private readonly object sync = new();
    private readonly Dictionary<string, List<Pod>> pods = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> desired = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> autoHeal = new(StringComparer.OrdinalIgnoreCase);
    private string? repositoryRoot;

    public IReadOnlyList<ServicePodsView> Snapshot()
    {
        lock (sync)
        {
            var now = DateTime.UtcNow;
            var scalable = Specs.Select(spec => new ServicePodsView(
                spec.Name, desired.GetValueOrDefault(spec.Name, 1), autoHeal.GetValueOrDefault(spec.Name, true), true,
                pods.GetValueOrDefault(spec.Name, []).OrderBy(pod => pod.Slot).Select(pod => View(pod, now)).ToList()));
            var fixedServices = Fixed.Select(item => new ServicePodsView(item.Name, 1, false, false,
                pods.GetValueOrDefault(item.Name, []).Select(pod => View(pod, now)).ToList()));
            return scalable.Concat(fixedServices).ToList();
        }
    }

    /// <summary>Destinations the gateway should balance across: running, not drained.</summary>
    public IReadOnlyDictionary<string, List<string>> Topology()
    {
        lock (sync)
        {
            return Specs.ToDictionary(
                spec => spec.Name,
                spec => pods.GetValueOrDefault(spec.Name, [])
                    .Where(pod => pod.State is "Running" or "Starting" && !pod.Drained)
                    .OrderBy(pod => pod.Slot)
                    .Select(pod => $"http://{pod.Address}")
                    .ToList());
        }
    }

    public void Scale(string service, int replicas)
    {
        var spec = Spec(service);
        replicas = Math.Clamp(replicas, 0, MaxReplicas);
        lock (sync)
        {
            var previous = desired.GetValueOrDefault(spec.Name, 1);
            desired[spec.Name] = replicas;
            events.Info("compute", spec.Name, $"Scaled {spec.Name} from {previous} to {replicas} replica{(replicas == 1 ? string.Empty : "s")}.");
        }
    }

    public void SetAutoHeal(string service, bool enabled)
    {
        var spec = Spec(service);
        lock (sync)
        {
            autoHeal[spec.Name] = enabled;
        }

        events.Info("compute", spec.Name, $"Self-healing {(enabled ? "enabled" : "disabled")} for {spec.Name}.");
    }

    public void Crash(string podId)
    {
        var pod = Find(podId);
        Kill(pod, "Crashed by admin (chaos test)");
        events.Critical("compute", pod.Id, $"Pod {pod.Id} on :{pod.Port} was killed. The gateway will route around it.");
    }

    public void Restart(string podId)
    {
        var pod = Find(podId);
        Kill(pod, "Restart requested");
        lock (sync)
        {
            pod.State = "Restarting";
        }

        Start(Spec(pod.Service), pod);
        events.Info("compute", pod.Id, $"Restarting pod {pod.Id}.");
    }

    /// <summary>Back to the baseline: one pod per service, self-healing on, nothing cordoned.</summary>
    public void ResetAll()
    {
        lock (sync)
        {
            foreach (var spec in Specs)
            {
                desired[spec.Name] = 1;
                autoHeal[spec.Name] = true;
                foreach (var pod in pods.GetValueOrDefault(spec.Name, []))
                {
                    pod.Drained = false;
                }
            }
        }
    }

    public void SetDrained(string podId, bool drained)
    {
        var pod = Find(podId);
        lock (sync)
        {
            pod.Drained = drained;
        }

        if (drained)
        {
            events.Warning("compute", pod.Id, $"Pod {pod.Id} cordoned: the gateway stops sending it traffic.");
        }
        else
        {
            events.Info("compute", pod.Id, $"Pod {pod.Id} back in rotation.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        repositoryRoot = FindRepositoryRoot(environment.ContentRootPath);
        Discover(initial: true);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Discover(initial: false);
                // With several Admin instances, only the elected coordinator restarts pods (no duplicate launches).
                if (leadership.IsLeader)
                {
                    Reconcile();
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Pod reconciliation failed.");
            }

            await Task.Delay(2_000, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    /// <summary>Matches listening ports to pods: adopts instances started elsewhere, notices crashes.</summary>
    private void Discover(bool initial)
    {
        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();
        var now = DateTime.UtcNow;
        lock (sync)
        {
            foreach (var spec in Specs)
            {
                var list = pods.TryGetValue(spec.Name, out var existing) ? existing : pods[spec.Name] = [];
                for (var slot = 0; slot < MaxReplicas; slot++)
                {
                    var port = PortFor(spec, slot);
                    var pod = list.FirstOrDefault(item => item.Slot == slot);
                    if (listening.Contains(port))
                    {
                        if (pod is null)
                        {
                            list.Add(new Pod { Id = $"{spec.Name}-{slot}", Service = spec.Name, Port = port, Slot = slot, State = "Running" });
                        }
                        else if (pod.State is "Starting" or "Restarting" or "Crashed")
                        {
                            if (pod.State != "Running")
                            {
                                pod.State = "Running";
                                if (!initial)
                                {
                                    events.Success("compute", pod.Id, $"Pod {pod.Id} is serving on :{pod.Port}.");
                                }
                            }
                        }
                    }
                    else if (pod is { State: "Running" })
                    {
                        pod.State = "Crashed";
                        pod.CrashedAtUtc = now;
                        pod.LastReason ??= "Process exited";
                        events.Critical("compute", pod.Id, $"Pod {pod.Id} stopped responding on :{pod.Port}.");
                    }
                    else if (pod is { State: "Starting" or "Restarting" } && now - pod.StartedAtUtc > TimeSpan.FromSeconds(45))
                    {
                        pod.State = "Crashed";
                        pod.CrashedAtUtc = now;
                        pod.LastReason = "Failed to start within 45s";
                    }
                }

                if (initial)
                {
                    desired[spec.Name] = Math.Max(1, list.Count(pod => pod.State == "Running"));
                    autoHeal[spec.Name] = true;
                }
            }

            foreach (var (name, port) in Fixed)
            {
                var running = listening.Contains(port);
                pods[name] = [new Pod { Id = $"{name}-0", Service = name, Port = port, Slot = 0, State = running ? "Running" : "Crashed" }];
            }
        }
    }

    /// <summary>Moves each service toward its desired replica count, like a ReplicaSet controller.</summary>
    private void Reconcile()
    {
        var toStart = new List<(ServiceSpec Spec, Pod Pod)>();
        var toStop = new List<Pod>();
        lock (sync)
        {
            var now = DateTime.UtcNow;
            foreach (var spec in Specs)
            {
                var list = pods[spec.Name];
                var target = desired.GetValueOrDefault(spec.Name, 1);
                var live = list.Where(pod => pod.State is "Running" or "Starting" or "Restarting").OrderBy(pod => pod.Slot).ToList();

                foreach (var extra in live.Skip(target).Reverse())
                {
                    toStop.Add(extra);
                }

                // Without self-healing a crashed pod still counts toward the target, so it stays down.
                var heal = autoHeal.GetValueOrDefault(spec.Name, true);
                var missing = target - live.Count - (heal ? 0 : list.Count(pod => pod.State == "Crashed"));
                if (missing <= 0)
                {
                    continue;
                }

                // Replace crashed pods first (with a short back-off), then fill new slots.
                foreach (var crashed in list.Where(pod => pod.State == "Crashed" && pod.Slot < MaxReplicas).OrderBy(pod => pod.Slot))
                {
                    if (missing == 0)
                    {
                        break;
                    }

                    // Back-off: hold the slot for the crashed pod instead of starting a new one elsewhere.
                    if (now - (crashed.CrashedAtUtc ?? now) < TimeSpan.FromSeconds(4))
                    {
                        missing--;
                        continue;
                    }

                    crashed.State = "Restarting";
                    crashed.Restarts++;
                    toStart.Add((spec, crashed));
                    missing--;
                }

                for (var slot = 0; slot < MaxReplicas && missing > 0; slot++)
                {
                    if (list.Any(pod => pod.Slot == slot))
                    {
                        continue;
                    }

                    var pod = new Pod { Id = $"{spec.Name}-{slot}", Service = spec.Name, Port = PortFor(spec, slot), Slot = slot, State = "Starting" };
                    list.Add(pod);
                    toStart.Add((spec, pod));
                    missing--;
                }
            }
        }

        foreach (var pod in toStop)
        {
            Kill(pod, "Scaled down");
            lock (sync)
            {
                pods[pod.Service].Remove(pod);
            }

            events.Info("compute", pod.Id, $"Pod {pod.Id} terminated (scale down).");
        }

        foreach (var (spec, pod) in toStart)
        {
            Start(spec, pod);
        }
    }

    private void Start(ServiceSpec spec, Pod pod)
    {
        if (repositoryRoot is null)
        {
            pod.State = "Crashed";
            pod.LastReason = "Repository root not found; cannot launch pods";
            return;
        }

        var projectDirectory = Path.Combine(repositoryRoot, "src", "Services", spec.Project, $"DigitalBanking.{spec.Project}.Api");
        var assembly = Path.Combine(projectDirectory, "bin", "Debug", "net8.0", $"DigitalBanking.{spec.Project}.Api.dll");
        if (!File.Exists(assembly))
        {
            pod.State = "Crashed";
            pod.LastReason = "Service is not built (run dotnet build)";
            return;
        }

        var logDirectory = Path.Combine(Path.GetTempPath(), "digitalbanking-pods");
        Directory.CreateDirectory(logDirectory);
        var info = new ProcessStartInfo("dotnet", $"\"{assembly}\" --urls http://localhost:{pod.Port}")
        {
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        var process = Process.Start(info)!;
        var log = new StreamWriter(Path.Combine(logDirectory, $"{pod.Id}.log"), append: true) { AutoFlush = true };
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) lock (log) log.WriteLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) lock (log) log.WriteLine(args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (sync)
        {
            pod.Process = process;
            pod.ProcessId = process.Id;
            pod.Managed = true;
            pod.StartedAtUtc = DateTime.UtcNow;
            pod.CrashedAtUtc = null;
            pod.LastReason = null;
            if (pod.State != "Restarting")
            {
                pod.State = "Starting";
            }
        }

        events.Info("compute", pod.Id, $"Launching pod {pod.Id} on :{pod.Port} (pid {process.Id}).");
    }

    private void Kill(Pod pod, string reason)
    {
        try
        {
            var process = pod.Process is { HasExited: false } owned ? owned : FindListeningProcess(pod.Port);
            process?.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Killing pod {Pod} failed.", pod.Id);
        }

        lock (sync)
        {
            pod.State = "Crashed";
            pod.CrashedAtUtc = DateTime.UtcNow;
            pod.LastReason = reason;
            pod.Process = null;
        }
    }

    /// <summary>Finds the process listening on a port (pods started outside the supervisor).</summary>
    private static Process? FindListeningProcess(int port)
    {
        var info = new ProcessStartInfo("netstat", "-ano -p TCP") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var netstat = Process.Start(info)!;
        var output = netstat.StandardOutput.ReadToEnd();
        netstat.WaitForExit(5_000);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && parts[3] == "LISTENING" && parts[1].EndsWith($":{port}", StringComparison.Ordinal) && int.TryParse(parts[4], out var pid))
            {
                return Process.GetProcessById(pid);
            }
        }

        return null;
    }

    private Pod Find(string podId)
    {
        lock (sync)
        {
            return pods.Values.SelectMany(list => list).FirstOrDefault(pod => string.Equals(pod.Id, podId, StringComparison.OrdinalIgnoreCase)
                && Specs.Any(spec => spec.Name == pod.Service))
                ?? throw new NotFoundException($"Pod '{podId}' was not found or cannot be managed.");
        }
    }

    private static ServiceSpec Spec(string service) =>
        Specs.FirstOrDefault(spec => string.Equals(spec.Name, service, StringComparison.OrdinalIgnoreCase))
        ?? throw new ValidationException($"'{service}' is not a scalable service.");

    private static int PortFor(ServiceSpec spec, int slot) => spec.BasePort + slot * 10;

    private static PodView View(Pod pod, DateTime now) => new(
        pod.Id, pod.Service, pod.Port, pod.Address, pod.State, pod.Managed, pod.Drained, pod.Restarts, pod.ProcessId,
        pod.State == "Running" ? Math.Round((now - pod.StartedAtUtc).TotalSeconds) : 0, pod.LastReason);

    private static string? FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DigitalBanking.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
