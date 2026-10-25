namespace DigitalBanking.Account.Api.Contracts;

public sealed class OpenAccountRequest
{
    public string AccountType { get; set; } = "Savings";
}

public sealed class MoneyMovementRequest
{
    public decimal Amount { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;

    // Deposit, Withdrawal, TransferOut, TransferIn or Refund: picks the general-ledger side of the posting.
    public string Purpose { get; set; } = string.Empty;
}

public sealed class OpenFundedAccountRequest
{
    public Guid UserId { get; set; }
    public string AccountType { get; set; } = "Savings";
    public decimal OpeningBalance { get; set; }
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
    public bool HasIdentityBackDocument { get; set; }
    public bool HasAddressDocument { get; set; }
    public bool HasSignature { get; set; }
    public string? IdentityDocumentUrl { get; set; }
    public string? IdentityBackDocumentUrl { get; set; }
    public string? AddressDocumentUrl { get; set; }
    public string? SignatureUrl { get; set; }
    public Guid? CreatedAccountId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class ReviewApplicationRequest
{
    public string Status { get; set; } = string.Empty;
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

// The account-opening form (multipart): documents are uploaded with the request.
public sealed class AccountApplicationForm
{
    public string AccountType { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public bool TermsAccepted { get; set; }
    public Microsoft.AspNetCore.Http.IFormFile? IdentityDocument { get; set; }
    public Microsoft.AspNetCore.Http.IFormFile? IdentityBackDocument { get; set; }
    public Microsoft.AspNetCore.Http.IFormFile? AddressDocument { get; set; }
    public Microsoft.AspNetCore.Http.IFormFile? Signature { get; set; }
}
