using DigitalBanking.Audit.Api.Application;
using DigitalBanking.BuildingBlocks.Security;
using DigitalBanking.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Audit.Api.Controllers;

// Two-phase commit participant endpoints, called by the coordinator in the transaction service.
[ApiController]
[InternalApiKey]
[Route("api/audit-logs/internal/2pc")]
public sealed class TwoPhaseController(TwoPhaseAuditParticipant participant) : ControllerBase
{
    [HttpPost("prepare")]
    public async Task<ActionResult<TwoPhaseVoteResponse>> Prepare(TwoPhasePrepareRequest request, CancellationToken cancellationToken)
    {
        return Ok(await participant.PrepareAsync(request, cancellationToken));
    }

    [HttpPost("commit")]
    public async Task<ActionResult<TwoPhaseAckResponse>> Commit(TwoPhaseDecisionRequest request, CancellationToken cancellationToken)
    {
        return Ok(await participant.CommitAsync(request, cancellationToken));
    }

    [HttpPost("abort")]
    public async Task<ActionResult<TwoPhaseAckResponse>> Abort(TwoPhaseDecisionRequest request, CancellationToken cancellationToken)
    {
        return Ok(await participant.AbortAsync(request, cancellationToken));
    }
}
