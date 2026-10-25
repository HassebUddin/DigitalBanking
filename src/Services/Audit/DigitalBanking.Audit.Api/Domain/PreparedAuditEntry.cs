using DigitalBanking.Contracts;

namespace DigitalBanking.Audit.Api.Domain;

// The audit service's side of a two-phase commit: the compliance record of a high-value operation, held as
// Prepared until the coordinator decides. On commit it becomes a normal audit log entry; on abort it stays here
// as Aborted (with the reason), so a refused approval is still traceable.
public sealed class PreparedAuditEntry
{
    public Guid TransactionId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string State { get; set; } = TwoPhaseStates.Prepared;
    public string? Reason { get; set; }
    public long LogicalTimestamp { get; set; }
    public DateTime PreparedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
}
