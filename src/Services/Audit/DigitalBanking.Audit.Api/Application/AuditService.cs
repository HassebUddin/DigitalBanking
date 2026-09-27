using DigitalBanking.Audit.Api.Domain;
using DigitalBanking.Audit.Api.Repository;

namespace DigitalBanking.Audit.Api.Application;

public sealed class AuditService(IAuditRepository auditRepository)
{
    public Task<IReadOnlyList<AuditLog>> GetAuditListAsync(string? eventType, CancellationToken cancellationToken)
    {
        return auditRepository.GetAuditListAsync(eventType, cancellationToken);
    }
}
