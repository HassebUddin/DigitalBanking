using DigitalBanking.Audit.Api.Application;
using DigitalBanking.Audit.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Audit.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/audit-logs")]
public sealed class AuditLogsController(AuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AuditLog>>> List([FromQuery] string? eventType, CancellationToken cancellationToken)
    {
        return Ok(await auditService.ListAsync(eventType, cancellationToken));
    }
}
