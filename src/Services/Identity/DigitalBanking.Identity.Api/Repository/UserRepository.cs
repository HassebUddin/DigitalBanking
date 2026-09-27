using DigitalBanking.Identity.Api.Domain;
using DigitalBanking.Identity.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Identity.Api.Repository;

public sealed class UserRepository(IdentityDbContext dbContext) : IUserRepository
{
    public Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);
    }

    public async Task<IReadOnlyList<User>> GetActiveUsersExceptAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await dbContext.Users.Where(user => user.Id != userId && user.IsActive).OrderBy(user => user.FullName).ThenBy(user => user.Email).ToListAsync(cancellationToken);
    }
}
