namespace DigitalBanking.Transaction.Api.Contracts;

public sealed class DepositRequest
{
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class WithdrawRequest
{
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class TransferRequest
{
    public Guid SourceAccountId { get; set; }
    public string DestinationAccountNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class TransactionResponse
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid? CounterpartyAccountId { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public long LogicalTimestamp { get; set; }
}

public sealed class TransactionSearchRequest
{
    public string? SearchText { get; set; }
    // Activity of one account: what it sent and what it received.
    public Guid? AccountId { get; set; }
    public decimal? MinimumAmount { get; set; }
    public decimal? MaximumAmount { get; set; }
    public string? TransactionType { get; set; }
    public DateTime? FromDateUtc { get; set; }
    public DateTime? ToDateUtc { get; set; }
    public string SortBy { get; set; } = "createdAt";
    public string SortDirection { get; set; } = "desc";
}

public sealed class StatementResponse
{
    public Guid AccountId { get; set; }
    public DateTime FromDateUtc { get; set; }
    public DateTime ToDateUtc { get; set; }
    public decimal TotalDeposits { get; set; }
    public decimal TotalWithdrawals { get; set; }
    public IReadOnlyList<TransactionResponse> Transactions { get; set; } = [];
}

// Staff approve a loan disbursement; it commits atomically across the transaction, account and audit services (2PC).
public sealed class LoanDisbursementRequest
{
    public Guid? AccountId { get; set; }
    public string? AccountNumber { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;

    // Drill: the coordinator "crashes" after phase 1, before logging a decision. The participants stay in doubt,
    // holding their reservations, until recovery presumes abort (this blocking window is the price of 2PC).
    public bool SimulateCrashBeforeDecision { get; set; }
}

public sealed class DistributedTransactionStep
{
    public long Hlc { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Step { get; set; } = string.Empty;
}

public sealed class DistributedTransactionResponse
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Region { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? AccountVote { get; set; }
    public string? AuditVote { get; set; }
    public string? AbortReason { get; set; }
    public bool AccountAcked { get; set; }
    public bool AuditAcked { get; set; }
    public double? PrepareMs { get; set; }
    public double? CommitMs { get; set; }
    public int DecisionAttempts { get; set; }
    public long LogicalTimestamp { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public List<DistributedTransactionStep> Log { get; set; } = [];
}
