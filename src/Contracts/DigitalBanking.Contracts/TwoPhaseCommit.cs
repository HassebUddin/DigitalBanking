namespace DigitalBanking.Contracts;

// Wire contracts of the two-phase commit between the coordinator (transaction service) and its participants
// (account service: the money; audit service: the compliance record).
public static class TwoPhaseVotes
{
    public const string Commit = "VoteCommit";
    public const string Abort = "VoteAbort";

    // No answer in time (participant down, fenced database...): counts as an abort vote.
    public const string NoReply = "NoReply";
}

// A participant's local record of its part in one distributed transaction.
public static class TwoPhaseStates
{
    public const string Prepared = "Prepared";
    public const string Committed = "Committed";
    public const string Aborted = "Aborted";
}

public sealed class TwoPhasePrepareRequest
{
    public Guid TransactionId { get; set; }
    public Guid AccountId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public Guid OwnerUserId { get; set; }
    public Guid ApprovedBy { get; set; }
    public decimal Amount { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class TwoPhaseDecisionRequest
{
    public Guid TransactionId { get; set; }
    public Guid AccountId { get; set; }
    public string Region { get; set; } = string.Empty;
}

public sealed class TwoPhaseVoteResponse
{
    public string Participant { get; set; } = string.Empty;
    public string Vote { get; set; } = TwoPhaseVotes.Abort;
    public string? Reason { get; set; }
    public long LogicalTimestamp { get; set; }
}

public sealed class TwoPhaseAckResponse
{
    public string Participant { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public long LogicalTimestamp { get; set; }
}

// A participant's in-doubt (prepared, undecided) transactions, for the admin console.
public sealed class TwoPhaseInDoubt
{
    public Guid TransactionId { get; set; }
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTime PreparedAtUtc { get; set; }
}
