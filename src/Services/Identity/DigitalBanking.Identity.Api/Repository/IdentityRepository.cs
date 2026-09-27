using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.Contracts;
using DigitalBanking.Identity.Api.Domain;
using DigitalBanking.Identity.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Identity.Api.Repository;

public sealed class IdentityRepository(IdentityDbContext dbContext) : IIdentityRepository
{
    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        return dbContext.Users.AnyAsync(user => user.Email == email, cancellationToken);
    }

    public Task<User?> GetUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        return dbContext.Users.FirstOrDefaultAsync(user => user.Email == email, cancellationToken);
    }

    public Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return dbContext.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken);
    }

    public void AddUser(User user)
    {
        dbContext.Users.Add(user);
    }

    public Task<RefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken)
    {
        return dbContext.RefreshTokens
            .Include(token => token.User)
            .FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);
    }

    public async Task RevokeActiveRefreshTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        var tokens = await dbContext.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.RevokedAtUtc = DateTime.UtcNow;
        }
    }

    public void AddRefreshToken(RefreshToken token)
    {
        dbContext.RefreshTokens.Add(token);
    }

    public void AddPasswordResetToken(PasswordResetToken token)
    {
        dbContext.PasswordResetTokens.Add(token);
    }

    public Task<PasswordResetToken?> GetUnusedResetTokenAsync(Guid userId, string tokenHash, CancellationToken cancellationToken)
    {
        return dbContext.PasswordResetTokens
            .Where(token => token.UserId == userId && token.TokenHash == tokenHash && token.UsedAtUtc == null)
            .OrderByDescending(token => token.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public void AddEvent<TEvent>(TEvent integrationEvent) where TEvent : IntegrationEvent
    {
        OutboxWriter.AddEvent(dbContext, integrationEvent);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
