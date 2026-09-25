namespace DigitalBanking.Account.Api.Domain;

public sealed class AccountApplication
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid CustomerId { get; set; }
    public string AccountType { get; set; } = AccountTypes.Savings;
    public string Purpose { get; set; } = string.Empty;
    public string IdentityDocumentUrl { get; set; } = string.Empty;
    public string AddressDocumentUrl { get; set; } = string.Empty;
    public string SignatureUrl { get; set; } = string.Empty;
    public bool TermsAccepted { get; set; }
    public string Status { get; set; } = ApplicationStatuses.Pending;
    public string ReviewNote { get; set; } = string.Empty;
    public Guid? CreatedAccountId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
}

public static class ApplicationStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}
