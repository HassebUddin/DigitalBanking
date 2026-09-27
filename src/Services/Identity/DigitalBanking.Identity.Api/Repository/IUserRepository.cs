using DigitalBanking.Identity.Api.Domain;

namespace DigitalBanking.Identity.Api.Repository;

public interface IUserRepository
{
    Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<User>> GetActiveUsersExceptAsync(Guid userId, CancellationToken cancellationToken);
}
