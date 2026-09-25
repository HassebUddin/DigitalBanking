using DigitalBanking.Account.Api.Application;
using DigitalBanking.Account.Api.Contracts;
using DigitalBanking.Account.Api.Domain;
using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Account.Api.Controllers;

[ApiController]
[Route("api/accounts")]
public sealed class AccountsController(
    AccountService accountService,
    CustomerLookupClient customerLookupClient,
    CurrentUser currentUser) : ControllerBase
{
    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<ActionResult<AccountResponse>> Open(OpenAccountRequest request, CancellationToken cancellationToken)
    {
        var accessToken = Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty);
        var customerId = await customerLookupClient.GetCustomerIdAsync(accessToken, cancellationToken);
        var account = await accountService.OpenAccountAsync(currentUser.UserId, customerId, request, cancellationToken);
        return Ok(account);
    }

    [Authorize(Roles = "Customer")]
    [HttpPost("applications")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(25_000_000)]
    public async Task<ActionResult<AccountApplicationResponse>> Apply(
        [FromForm] string accountType,
        [FromForm] string purpose,
        [FromForm] bool termsAccepted,
        [FromForm] IFormFile identityDocument,
        [FromForm] IFormFile addressDocument,
        [FromForm] IFormFile signature,
        [FromServices] AccountFileStore fileStore,
        CancellationToken cancellationToken)
    {
        var accessToken = Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty);
        var customerId = await customerLookupClient.GetCustomerIdAsync(accessToken, cancellationToken);
        return Ok(await accountService.SubmitApplicationAsync(
            currentUser.UserId,
            customerId,
            accountType,
            purpose,
            identityDocument,
            addressDocument,
            signature,
            termsAccepted,
            fileStore,
            cancellationToken));
    }

    [Authorize]
    [HttpGet("applications")]
    public async Task<ActionResult<IReadOnlyList<AccountApplicationResponse>>> Applications(CancellationToken cancellationToken)
    {
        return Ok(await accountService.ListApplicationsAsync(currentUser.UserId, currentUser.IsAdmin, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("applications/{applicationId:guid}/approve")]
    public async Task<ActionResult<AccountApplicationResponse>> Approve(Guid applicationId, [FromBody] ReviewApplicationRequest? request, CancellationToken cancellationToken)
    {
        return Ok(await accountService.ApproveApplicationAsync(applicationId, request?.Note ?? string.Empty, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("applications/{applicationId:guid}/reject")]
    public async Task<ActionResult<AccountApplicationResponse>> Reject(Guid applicationId, [FromBody] ReviewApplicationRequest? request, CancellationToken cancellationToken)
    {
        return Ok(await accountService.RejectApplicationAsync(applicationId, request?.Note ?? string.Empty, cancellationToken));
    }

    [Authorize]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AccountResponse>>> List(CancellationToken cancellationToken)
    {
        return Ok(await accountService.ListForUserAsync(currentUser.UserId, currentUser.IsAdmin, cancellationToken));
    }

    [Authorize]
    [HttpGet("{accountId:guid}")]
    public async Task<ActionResult<AccountResponse>> Get(Guid accountId, CancellationToken cancellationToken)
    {
        return Ok(await accountService.GetAsync(accountId, currentUser.UserId, currentUser.IsAdmin, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{accountId:guid}/freeze")]
    public async Task<ActionResult<AccountResponse>> Freeze(Guid accountId, CancellationToken cancellationToken)
    {
        return Ok(await accountService.ChangeStatusAsync(accountId, AccountStatuses.Frozen, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{accountId:guid}/unfreeze")]
    public async Task<ActionResult<AccountResponse>> Unfreeze(Guid accountId, CancellationToken cancellationToken)
    {
        return Ok(await accountService.ChangeStatusAsync(accountId, AccountStatuses.Active, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{accountId:guid}/close")]
    public async Task<ActionResult<AccountResponse>> Close(Guid accountId, CancellationToken cancellationToken)
    {
        return Ok(await accountService.ChangeStatusAsync(accountId, AccountStatuses.Closed, cancellationToken));
    }

    [InternalApiKey]
    [HttpGet("internal/{accountId:guid}")]
    public async Task<ActionResult<AccountResponse>> GetInternal(Guid accountId, CancellationToken cancellationToken)
    {
        return Ok(await accountService.GetInternalAsync(accountId, cancellationToken));
    }

    [InternalApiKey]
    [HttpGet("internal/by-number/{accountNumber}")]
    public async Task<ActionResult<AccountResponse>> GetByNumber(string accountNumber, CancellationToken cancellationToken)
    {
        return Ok(await accountService.GetByAccountNumberAsync(accountNumber, cancellationToken));
    }

    [InternalApiKey]
    [HttpPost("internal/{accountId:guid}/debit")]
    public async Task<IActionResult> Debit(Guid accountId, MoneyMovementRequest request, CancellationToken cancellationToken)
    {
        await accountService.DebitAsync(accountId, request, cancellationToken);
        return NoContent();
    }

    [InternalApiKey]
    [HttpPost("internal/{accountId:guid}/credit")]
    public async Task<IActionResult> Credit(Guid accountId, MoneyMovementRequest request, CancellationToken cancellationToken)
    {
        await accountService.CreditAsync(accountId, request, cancellationToken);
        return NoContent();
    }
}
