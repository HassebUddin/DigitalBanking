namespace DigitalBanking.Transaction.Api.Domain;

public sealed class BankTransaction
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid UserId { get; set; }
    public Guid? CounterpartyAccountId { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = TransactionStatuses.Pending;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class TransferSaga
{
    public Guid Id { get; set; }
    public Guid TransactionId { get; set; }
    public Guid SourceAccountId { get; set; }
    public Guid DestinationAccountId { get; set; }
    public decimal Amount { get; set; }
    public string State { get; set; } = TransferSagaStates.Started;
}

public static class TransactionTypes
{
    public const string Deposit = "Deposit";
    public const string Withdrawal = "Withdrawal";
    public const string Transfer = "Transfer";
}

public static class TransactionStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Compensated = "Compensated";
}

public static class TransferSagaStates
{
    public const string Started = "Started";
    public const string Debited = "Debited";
    public const string Completed = "Completed";
    public const string Compensated = "Compensated";
    public const string Failed = "Failed";
}
