using DigitalBanking.BuildingBlocks.Distributed;

namespace DigitalBanking.Admin.Api.Ops.Distributed;

public sealed record ClockEvent(int Step, string Node, string Event, DateTime WallClockUtc, long Hlc, string HlcText);

public sealed record ClockDriftResult(int SkewMs, IReadOnlyList<ClockEvent> Events, IReadOnlyList<string> WallClockOrder, IReadOnlyList<string> HlcOrder, bool WallClockWrong);

// Shows why transactions are ordered by hybrid logical clock rather than DateTime.UtcNow: two services whose
// clocks disagree by skewMs. The transaction service (clock ahead) records a deposit and sends it to the account
// service (clock behind), which then records the withdrawal it allowed because of that deposit.
// By wall clock the withdrawal comes first (impossible); by HLC the deposit always comes first.
public static class ClockDriftDemo
{
    public static ClockDriftResult Run(int skewMs)
    {
        skewMs = Math.Clamp(skewMs, 0, 60_000);
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long real = start;
        var ahead = new HybridLogicalClock(() => real + skewMs);
        var behind = new HybridLogicalClock(() => real);
        var events = new List<ClockEvent>();

        void Record(string node, string name, long wall, long hlc) =>
            events.Add(new ClockEvent(events.Count + 1, node, name, DateTimeOffset.FromUnixTimeMilliseconds(wall).UtcDateTime, hlc, HybridLogicalClock.Format(hlc)));

        var deposit = ahead.Now();
        Record("transaction-service (clock +" + skewMs + " ms)", "Deposit PKR 50,000 committed", real + skewMs, deposit);

        real += 3;                                  // the message takes 3 ms to arrive
        behind.Receive(deposit);                    // merged from the message (RabbitMQ payload / X-Hlc header)
        var withdrawal = behind.Now();
        Record("account-service (clock exact)", "Withdrawal PKR 45,000 allowed by that deposit", real, withdrawal);

        real += 2;
        var statement = behind.Now();
        Record("account-service (clock exact)", "Statement generated", real, statement);

        var wallOrder = events.OrderBy(item => item.WallClockUtc).ThenBy(item => item.Step).Select(item => item.Event).ToList();
        var hlcOrder = events.OrderBy(item => item.Hlc).Select(item => item.Event).ToList();
        return new ClockDriftResult(skewMs, events, wallOrder, hlcOrder, !wallOrder.SequenceEqual(hlcOrder));
    }
}
