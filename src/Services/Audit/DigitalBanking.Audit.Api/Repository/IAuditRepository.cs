using DigitalBanking.Audit.Api.Domain;

namespace DigitalBanking.Audit.Api.Repository;

public interface IAuditRepository
{
    Task<IReadOnlyList<AuditLog>> GetAuditListAsync(string? eventType, CancellationToken cancellationToken);
}
