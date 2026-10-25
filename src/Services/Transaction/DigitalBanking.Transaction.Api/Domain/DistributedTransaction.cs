namespace DigitalBanking.Transaction.Api.Domain;

// The coordinator's log of one two-phase commit (kept in the province database of the account it pays into,
// next to the BankTransaction with the same id). It is written, and confirmed on the standby, twice before any
// participant is told the outcome: once before phase 1 (Preparing) and once when the decision is taken
// (Committing / Aborting). That second write is the commit point; recovery finishes phase 2 from it.
public sealed class DistributedTransaction
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = DistributedTransactionKinds.LoanDisbursement;
    public Guid AccountId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public Guid OwnerUserId { get; set; }
    public Guid ApprovedBy { get; set; }
    public decimal Amount { get; set; }
    public string Region { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string State { get; set; } = CoordinatorStates.Preparing;

    // Phase 1 votes: VoteCommit, VoteAbort or NoReply (null while not answered yet).
    public string? AccountVote { get; set; }
    public string? AuditVote { get; set; }
    public string? AbortReason { get; set; }

    // Phase 2 acknowledgements.
    public bool AccountAcked { get; set; }
    public bool AuditAcked { get; set; }

    public double? PrepareMs { get; set; }
    public double? CommitMs { get; set; }
    public int DecisionAttempts { get; set; }

    // Protocol steps as a JSON array of { hlc, step }, for the admin console.
    public string Log { get; set; } = "[]";
    public long LogicalTimestamp { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public static class DistributedTransactionKinds
{
    public const string LoanDisbursement = "LoanDisbursement";
}

public static class CoordinatorStates
{
    // Phase 1 in progress; if the coordinator dies here, recovery presumes abort.
    public const string Preparing = "Preparing";

    // Decided (durably) and telling the participants.
    public const string Committing = "Committing";
    public const string Aborting = "Aborting";

    // Every participant acknowledged the decision.
    public const string Committed = "Committed";
    public const string Aborted = "Aborted";
}
