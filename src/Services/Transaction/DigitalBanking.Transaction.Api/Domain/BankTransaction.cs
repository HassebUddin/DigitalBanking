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

    // Hybrid logical clock at creation (BuildingBlocks/Distributed/HybridLogicalClock): orders transactions by
    // cause and effect across services, which wall-clock CreatedAtUtc cannot do when machine clocks drift.
    // 0 on rows written before the clock existed.
    public long LogicalTimestamp { get; set; }
}

public sealed class TransferSaga
{
    public Guid Id { get; set; }
    public Guid TransactionId { get; set; }
    public Guid SourceAccountId { get; set; }
    public Guid DestinationAccountId { get; set; }
    public decimal Amount { get; set; }
    public string State { get; set; } = TransferSagaStates.Started;
    // Set when the transfer was accepted while the account service was down (DestinationAccountId is then empty).
    public string? DestinationAccountNumber { get; set; }
    // Set when the request handed the transfer to the recovery job; null while a request is still working on it.
    public DateTime? RetryAfterUtc { get; set; }
}

public static class TransactionTypes
{
    public const string Deposit = "Deposit";
    public const string Withdrawal = "Withdrawal";
    public const string Transfer = "Transfer";

    // Money returned to the sender of a transfer that could not be delivered (rollback).
    public const string Refund = "Refund";

    // A loan paid into a customer's account, approved by staff and committed with two-phase commit.
    public const string LoanDisbursement = "LoanDisbursement";
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
    public const string Compensating = "Compensating";
    public const string Compensated = "Compensated";
    public const string Failed = "Failed";
}

public static class TransferSagaMachine
{
    public const string DebitSource = "DebitSource";
    public const string CreditDestination = "CreditDestination";
    public const string RefundSource = "RefundSource";
    public const string Stop = "Stop";

    public static string NextAction(string state) => state switch
    {
        TransferSagaStates.Started => DebitSource,
        TransferSagaStates.Debited => CreditDestination,
        TransferSagaStates.Compensating => RefundSource,
        _ => Stop
    };
}
