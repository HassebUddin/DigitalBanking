namespace DigitalBanking.Audit.Api.Domain;

public sealed class AuditLog
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}
