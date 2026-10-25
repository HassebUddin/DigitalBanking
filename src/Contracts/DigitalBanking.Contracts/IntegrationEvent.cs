namespace DigitalBanking.Contracts;

public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;

    // Hybrid logical clock of the sender when the event was written (BuildingBlocks/Distributed/HybridLogicalClock).
    // Receivers merge it into their own clock, so everything they do afterwards is ordered after this event.
    public long LogicalTimestamp { get; set; }
}
