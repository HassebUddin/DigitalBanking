using System.Text.Json;

namespace DigitalBanking.BuildingBlocks.Distributed;

// Hybrid logical clock (Kulkarni et al.): a 64-bit timestamp = 48 bits of wall-clock milliseconds + 16 bits of
// logical counter. Like a Lamport clock it never goes backwards and is always greater than every timestamp this
// process has seen (sent or received), so if event A could have caused event B, then HLC(A) < HLC(B), even when the
// machines' clocks drift. Unlike a plain Lamport counter it stays close to real time, so it still reads as a date.
//
// One clock per process (a service instance is one node). Every outgoing message and HTTP call carries the
// current value; every incoming one is merged with Receive: local = max(local, remote, now) + 1.
public sealed class HybridLogicalClock
{
    public static HybridLogicalClock Instance { get; } = new();

    public const string HeaderName = "X-Hlc";

    private const int CounterBits = 16;
    private const long CounterMask = (1L << CounterBits) - 1;

    private readonly object gate = new();
    private long last;

    private readonly Func<long> wallClockMs;

    public HybridLogicalClock(Func<long>? wallClockMs = null)
    {
        this.wallClockMs = wallClockMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // A timestamp for a local event (a transaction created here, a message sent from here).
    public long Now()
    {
        lock (gate)
        {
            var physical = wallClockMs() << CounterBits;
            last = physical > last ? physical : last + 1;
            return last;
        }
    }

    // Merge a timestamp seen on an incoming message or response: afterwards Now() is greater than it.
    public long Receive(long remote)
    {
        lock (gate)
        {
            var physical = wallClockMs() << CounterBits;
            last = Math.Max(Math.Max(last, remote) + 1, physical);
            return last;
        }
    }

    public long Current
    {
        get
        {
            lock (gate)
            {
                return last;
            }
        }
    }

    public static DateTime PhysicalTime(long timestamp) =>
        DateTimeOffset.FromUnixTimeMilliseconds(timestamp >> CounterBits).UtcDateTime;

    public static long Counter(long timestamp) => timestamp & CounterMask;

    // Rows written before the clock existed sort by their creation time.
    public static long FromDateTime(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() << CounterBits;

    // "2026-10-25T09:14:03.120Z+3": wall time plus the logical counter.
    public static string Format(long timestamp) =>
        $"{PhysicalTime(timestamp):yyyy-MM-ddTHH:mm:ss.fffZ}+{Counter(timestamp)}";

    // Integration events carry "logicalTimestamp" in their JSON; merging it on receipt keeps causal order across RabbitMQ.
    public void ReceiveFromPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("logicalTimestamp", out var value) && value.TryGetInt64(out var remote) && remote > 0)
            {
                Receive(remote);
            }
        }
        catch (JsonException)
        {
        }
    }
}
