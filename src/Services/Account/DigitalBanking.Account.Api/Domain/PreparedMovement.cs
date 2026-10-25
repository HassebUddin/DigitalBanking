using DigitalBanking.Contracts;

namespace DigitalBanking.Account.Api.Domain;

// This service's side of a two-phase commit: a credit it voted to commit in phase 1 and has not yet been told
// the outcome of. While Prepared it reserves the account: no other distributed transaction can prepare on it
// (filtered unique index) and it cannot be frozen or closed, so the promise "I can commit" stays true.
public sealed class PreparedMovement
{
    // The coordinator's global transaction id.
    public Guid TransactionId { get; set; }
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string Purpose { get; set; } = MovementPurposes.LoanDisbursement;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string State { get; set; } = TwoPhaseStates.Prepared;
    public string? Reason { get; set; }
    public long LogicalTimestamp { get; set; }
    public DateTime PreparedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
}
