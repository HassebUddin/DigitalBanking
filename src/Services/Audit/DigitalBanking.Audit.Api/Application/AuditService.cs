using DigitalBanking.Audit.Api.Domain;
using DigitalBanking.Audit.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Audit.Api.Application;

public sealed class AuditService(AuditDbContext dbContext)
{
    public async Task<IReadOnlyList<AuditLog>> ListAsync(string? eventType, CancellationToken cancellationToken)
    {
        var query = dbContext.AuditLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            query = query.Where(log => log.EventType == eventType);
        }

        return await query
            .OrderByDescending(log => log.OccurredAtUtc)
            .Take(300)
            .ToListAsync(cancellationToken);
    }
}
