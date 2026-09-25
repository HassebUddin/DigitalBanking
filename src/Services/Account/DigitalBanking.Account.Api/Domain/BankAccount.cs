namespace DigitalBanking.Account.Api.Domain;

public sealed class BankAccount
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid UserId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public string Currency { get; set; } = "PKR";
    public string Status { get; set; } = AccountStatuses.Active;
    public DateTime CreatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public static class AccountStatuses
{
    public const string Active = "Active";
    public const string Frozen = "Frozen";
    public const string Closed = "Closed";
}

public static class AccountTypes
{
    public const string Savings = "Savings";
    public const string Current = "Current";
}
