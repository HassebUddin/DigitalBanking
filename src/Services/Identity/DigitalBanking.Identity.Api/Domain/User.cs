namespace DigitalBanking.Identity.Api.Domain;

public sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public List<RefreshToken> RefreshTokens { get; set; } = [];
    public List<PasswordResetToken> PasswordResetTokens { get; set; } = [];
}
