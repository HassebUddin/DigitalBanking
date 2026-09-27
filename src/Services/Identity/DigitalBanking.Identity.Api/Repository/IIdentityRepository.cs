using DigitalBanking.Contracts;
using DigitalBanking.Identity.Api.Domain;

namespace DigitalBanking.Identity.Api.Repository;

public interface IIdentityRepository
{
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);
    Task<User?> GetUserByEmailAsync(string email, CancellationToken cancellationToken);
    Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken);
    void AddUser(User user);

    Task<RefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken);
    Task RevokeActiveRefreshTokensAsync(Guid userId, CancellationToken cancellationToken);
    void AddRefreshToken(RefreshToken token);

    void AddPasswordResetToken(PasswordResetToken token);
    Task<PasswordResetToken?> GetUnusedResetTokenAsync(Guid userId, string tokenHash, CancellationToken cancellationToken);

    void AddEvent<TEvent>(TEvent integrationEvent) where TEvent : IntegrationEvent;
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
