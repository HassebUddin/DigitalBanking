using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.BuildingBlocks.Inbox;

public interface IInboxDbContext
{
    DbSet<InboxMessage> InboxMessages { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
