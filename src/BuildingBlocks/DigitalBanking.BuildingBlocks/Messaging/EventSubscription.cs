namespace DigitalBanking.BuildingBlocks.Messaging;

public sealed class EventSubscription
{
    public required string EventType { get; init; }
    public required Func<string, CancellationToken, Task> Handler { get; init; }
}
