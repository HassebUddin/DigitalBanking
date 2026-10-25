using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.Transaction.Api.Application;
using DigitalBanking.Transaction.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Transaction.Api.Controllers;

// Two-phase commit (coordinator side): loan disbursements approved by staff.
[ApiController]
[Route("api/transactions/2pc")]
public sealed class DistributedTransactionsController(TwoPhaseCoordinator coordinator, CurrentUser currentUser) : ControllerBase
{
    [Authorize(Roles = "Admin,InternalEmployee")]
    [HttpPost("loan-disbursements")]
    public async Task<ActionResult<DistributedTransactionResponse>> DisburseLoan(LoanDisbursementRequest request, CancellationToken cancellationToken)
    {
        return Ok(await coordinator.DisburseLoanAsync(currentUser.UserId, request, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DistributedTransactionResponse>>> Recent([FromQuery] int take, CancellationToken cancellationToken)
    {
        return Ok(await coordinator.RecentAsync(Math.Clamp(take == 0 ? 20 : take, 1, 100), cancellationToken));
    }
}
