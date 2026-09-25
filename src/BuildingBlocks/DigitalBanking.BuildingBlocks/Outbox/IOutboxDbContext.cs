using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.BuildingBlocks.Outbox;

public interface IOutboxDbContext
{
    DbSet<OutboxMessage> OutboxMessages { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
