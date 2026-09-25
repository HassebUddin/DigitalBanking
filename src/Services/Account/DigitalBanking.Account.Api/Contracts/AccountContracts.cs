namespace DigitalBanking.Account.Api.Contracts;

public sealed class OpenAccountRequest
{
    public string AccountType { get; set; } = "Savings";
}

public sealed class MoneyMovementRequest
{
    public decimal Amount { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
}

public sealed class AccountApplicationResponse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string AccountType { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ReviewNote { get; set; } = string.Empty;
    public bool HasIdentityDocument { get; set; }
    public bool HasAddressDocument { get; set; }
    public bool HasSignature { get; set; }
    public Guid? CreatedAccountId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class ReviewApplicationRequest
{
    public string Note { get; set; } = string.Empty;
}

public sealed class AccountResponse
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid UserId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
