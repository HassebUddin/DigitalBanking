namespace DigitalBanking.Customer.Api.Domain;

public sealed class CustomerProfile
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NationalId { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string KycStatus { get; set; } = "Pending";
    // Province whose database holds this profile (punjab, sindh, kp, balochistan).
    public string Region { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
