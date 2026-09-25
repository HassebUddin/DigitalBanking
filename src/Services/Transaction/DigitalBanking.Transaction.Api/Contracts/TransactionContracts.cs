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
}

public sealed class TransactionSearchRequest
{
    public string? SearchText { get; set; }
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
