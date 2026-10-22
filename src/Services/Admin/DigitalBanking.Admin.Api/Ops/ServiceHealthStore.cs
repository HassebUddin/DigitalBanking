using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DigitalBanking.BuildingBlocks.Observability;

namespace DigitalBanking.Admin.Api.Ops;

public sealed record ServiceHealth(
    string Service,
    string Status,
    double CpuPercent,
    double MemoryMb,
    double GcHeapMb,
    int Threads,
    int InFlightRequests,
    long TotalRequests,
    double UptimeMinutes,
    double SecondsSinceHeartbeat,
    double[] CpuSeries,
    int Pods);

public sealed record InstanceHealth(
    string Service, string Instance, int ProcessId, double CpuPercent, double MemoryMb, int InFlightRequests, long TotalRequests,
    double SecondsSinceHeartbeat, double[] CpuSeries);

/// <summary>
/// Latest heartbeat per pod (service + address). Service health is the aggregate of its live pods,
/// so one crashed pod degrades a service instead of taking it down.
/// </summary>
public sealed class ServiceHealthStore
{
    private static readonly string[] KnownServices =
        ["gateway", "identity", "customer", "account", "transaction", "notification", "audit", "admin", "chat"];

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private volatile List<DestinationHealthDto> destinations = [];

    public IReadOnlyList<DestinationHealthDto> GatewayDestinations => destinations;

    public void Record(ServiceHeartbeat heartbeat)
    {
        var entry = entries.GetOrAdd($"{heartbeat.Service}|{heartbeat.Instance}", _ => new Entry());
        lock (entry)
        {
            entry.Latest = heartbeat;
            entry.ReceivedAtUtc = DateTime.UtcNow;
            entry.Cpu.Enqueue(heartbeat.CpuPercent);
            while (entry.Cpu.Count > 60)
            {
                entry.Cpu.Dequeue();
            }
        }

        if (heartbeat.Service == "gateway" && heartbeat.Destinations is not null)
        {
            destinations = heartbeat.Destinations;
        }
    }

    public IReadOnlyList<InstanceHealth> Instances()
    {
        var now = DateTime.UtcNow;
        var result = new List<InstanceHealth>();
        foreach (var entry in entries.Values)
        {
            lock (entry)
            {
                if (entry.Latest is not { } heartbeat || now - entry.ReceivedAtUtc > TimeSpan.FromMinutes(2))
                {
                    continue;
                }

                result.Add(new InstanceHealth(heartbeat.Service, heartbeat.Instance, heartbeat.ProcessId, heartbeat.CpuPercent, heartbeat.MemoryMb,
                    heartbeat.InFlightRequests, heartbeat.TotalRequests, Math.Round((now - entry.ReceivedAtUtc).TotalSeconds, 1), entry.Cpu.ToArray()));
            }
        }

        return result;
    }

    public IReadOnlyList<ServiceHealth> Snapshot()
    {
        var instances = Instances();
        return KnownServices.Union(instances.Select(item => item.Service), StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            var live = instances.Where(item => item.Service == name && item.SecondsSinceHeartbeat <= 10).ToList();
            if (live.Count == 0)
            {
                var last = instances.Where(item => item.Service == name).OrderBy(item => item.SecondsSinceHeartbeat).FirstOrDefault();
                return new ServiceHealth(name, "down", 0, 0, 0, 0, 0, 0, 0, last?.SecondsSinceHeartbeat ?? -1, [], 0);
            }

            var peakCpu = live.Max(item => item.CpuPercent);
            var status = peakCpu >= 80 ? "critical" : peakCpu >= 50 ? "degraded" : "healthy";
            var series = live.OrderByDescending(item => item.CpuSeries.Length).First().CpuSeries
                .Select((_, index) => live.Max(item => index < item.CpuSeries.Length ? item.CpuSeries[index] : 0)).ToArray();
            entries.TryGetValue($"{live[0].Service}|{live[0].Instance}", out var any);
            var threads = 0;
            var gcHeap = 0d;
            var uptime = 0d;
            if (any?.Latest is { } sample)
            {
                threads = sample.Threads;
                gcHeap = sample.GcHeapMb;
                uptime = Math.Round((DateTime.UtcNow - sample.StartedAtUtc).TotalMinutes, 1);
            }

            return new ServiceHealth(name, status, Math.Round(peakCpu, 1), Math.Round(live.Sum(item => item.MemoryMb), 1), gcHeap, threads,
                live.Sum(item => item.InFlightRequests), live.Sum(item => item.TotalRequests), uptime,
                live.Min(item => item.SecondsSinceHeartbeat), series, live.Count);
        }).ToList();
    }

    private sealed class Entry
    {
        public ServiceHeartbeat? Latest;
        public DateTime ReceivedAtUtc;
        public readonly Queue<double> Cpu = new();
    }
}
/// <summary>Machine-wide CPU usage from the Windows kernel; returns -1 elsewhere.</summary>
public sealed class HostMetrics
{
    private readonly object sync = new();
    private ulong lastIdle;
    private ulong lastTotal;
    private double lastValue;
    private DateTime lastRead = DateTime.MinValue;

    public double CpuPercent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return -1;
        }

        lock (sync)
        {
            if (DateTime.UtcNow - lastRead < TimeSpan.FromMilliseconds(800))
            {
                return lastValue;
            }

            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return lastValue;
            }

            var idleTicks = idle.Value;
            var total = kernel.Value + user.Value;
            if (lastTotal > 0 && total > lastTotal)
            {
                lastValue = Math.Round((1 - (idleTicks - lastIdle) / (double)(total - lastTotal)) * 100, 1);
            }

            lastIdle = idleTicks;
            lastTotal = total;
            lastRead = DateTime.UtcNow;
            return lastValue;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
}
