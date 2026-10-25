namespace DigitalBanking.Account.Api.Domain;

// One leg of a double-entry posting. Every money movement writes two legs in the same database
// transaction as the balance change: the customer's account and a general-ledger account, with
// debits equal to credits. A customer's balance is therefore always the sum of its own legs.
public sealed class LedgerEntry
{
    public Guid Id { get; set; }

    // The money movement this posting belongs to (e.g. the transfer's reference number); a cross-province
    // transfer has one posting in each province under the same journal id.
    public string JournalId { get; set; } = string.Empty;

    // Unique per posting: "{reference}:{D|C}:{accountId}". A retried debit or credit finds it and is not applied twice.
    public string IdempotencyKey { get; set; } = string.Empty;

    // 1 = customer leg, 2 = general-ledger leg.
    public int Leg { get; set; }

    // "CUST:<account number>" or a general-ledger account ("GL:CASH", "GL:CLEARING", "GL:OPENING").
    public string LedgerAccount { get; set; } = string.Empty;
    public Guid? AccountId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal? BalanceAfter { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public static class LedgerAccounts
{
    // Cash in the branch vaults: deposits and withdrawals.
    public const string Cash = "GL:CASH";

    // Money between two customer accounts (possibly in different provinces). Zero bank-wide once every transfer has completed.
    public const string Clearing = "GL:CLEARING";

    // Loans the bank has paid out (asset side): a disbursement debits it and credits the customer.
    public const string Loans = "GL:LOANS";

    // Balances that existed before the ledger.
    public const string Opening = "GL:OPENING";

    public static string Customer(string accountNumber) => $"CUST:{accountNumber}";
}

public static class MovementPurposes
{
    public const string Deposit = "Deposit";
    public const string Withdrawal = "Withdrawal";
    public const string TransferOut = "TransferOut";
    public const string TransferIn = "TransferIn";
    public const string Refund = "Refund";
    public const string Opening = "Opening";
    public const string LoanDisbursement = "LoanDisbursement";

    public static string CounterAccount(string purpose) => purpose switch
    {
        Deposit or Withdrawal => LedgerAccounts.Cash,
        Opening => LedgerAccounts.Opening,
        LoanDisbursement => LedgerAccounts.Loans,
        _ => LedgerAccounts.Clearing
    };

    public static string Normalize(string? purpose, string fallback) =>
        new[] { Deposit, Withdrawal, TransferOut, TransferIn, Refund, Opening, LoanDisbursement }
            .FirstOrDefault(known => string.Equals(known, purpose?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;
}
