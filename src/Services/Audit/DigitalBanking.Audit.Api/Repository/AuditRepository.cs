using DigitalBanking.Audit.Api.Domain;
using DigitalBanking.Audit.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Regions;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Audit.Api.Repository;

// Audit entries are spread over the 4 province databases; the admin sees them as one list.
public sealed class AuditRepository(RegionalDb<AuditDbContext> db) : IAuditRepository
{
    public async Task<IReadOnlyList<AuditLog>> GetAuditListAsync(string? eventType, CancellationToken cancellationToken)
    {
        var logs = await db.AllAsync(context =>
        {
            var query = context.AuditLogs.AsQueryable();
            if (!string.IsNullOrWhiteSpace(eventType))
                query = query.Where(log => log.EventType == eventType);

            return query.OrderByDescending(log => log.OccurredAtUtc).Take(300).ToListAsync(cancellationToken);
        });

        return logs.OrderByDescending(log => log.OccurredAtUtc).Take(300).ToList();
    }
}
