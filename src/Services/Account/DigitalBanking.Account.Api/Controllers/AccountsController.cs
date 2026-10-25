using DigitalBanking.Account.Api.Application;
using DigitalBanking.Account.Api.Contracts;
using DigitalBanking.Account.Api.Domain;
using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts;
using DigitalBanking.Contracts.Events;
using DigitalBanking.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Account.Api.Controllers;

[ApiController]
[Route("api/accounts")]
public sealed class AccountsController(
    AccountService accountService,
    CustomerLookupClient customerLookupClient,
    CurrentUser currentUser,
    IEventPublisher eventPublisher) : ControllerBase
{
    [Authorize(Roles = "Admin")]
    [HttpPost("add-account")]
    public async Task<ActionResult<AccountResponse>> AddAccount(OpenAccountRequest request, CancellationToken cancellationToken)
    {
        var accessToken = Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty);
        Guid customerId;
        try
        {
            customerId = await customerLookupClient.GetCustomerIdAsync(accessToken, cancellationToken);
        }
        catch (ServiceUnavailableException)
        {
            // The customer's province is down: the request is queued and the profile is looked up when it is back.
            customerId = currentUser.CustomerId ?? Guid.Empty;
        }

    
        var account = await accountService.AddAccountAsync(currentUser.UserId, customerId, request, cancellationToken);
        return Ok(account);
    }

    // An account for any user, funded with an opening deposit (drills, benchmarks, staff tooling).
    [Authorize(Roles = "Admin")]
    [HttpPost("admin/open-funded-account")]
    public async Task<ActionResult<AccountResponse>> OpenFundedAccount(OpenFundedAccountRequest request, CancellationToken cancellationToken)
    {
        return Ok(await accountService.OpenFundedAccountAsync(request, cancellationToken));
    }

    // Double-entry invariants across every province's ledger.
    [Authorize(Roles = "Admin")]
    [HttpGet("ledger/verify")]
    public async Task<IActionResult> VerifyLedger([FromServices] LedgerAuditor auditor, CancellationToken cancellationToken)
    {
        return Ok(await auditor.VerifyAsync(cancellationToken));
    }

    [Authorize(Roles = "Customer")]
    [HttpPost("request-account-application")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(25_000_000)]
    public async Task<ActionResult<AccountApplicationResponse>> RequestAccountApplication([FromForm] string accountType, [FromForm] string purpose, [FromForm] bool termsAccepted, [FromForm] IFormFile? identityDocument, [FromForm] IFormFile? identityBackDocument, [FromForm] IFormFile? addressDocument, [FromForm] IFormFile? signature, [FromServices] AccountFileStore fileStore, CancellationToken cancellationToken)
    {
        if (identityDocument is null || identityBackDocument is null || signature is null) throw new ValidationException("CNIC front, CNIC back and signature are required.");
        if (!termsAccepted) throw new ValidationException("You must accept the account opening declaration.");
        var accessToken = Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty);

        Guid customerId;
        try
        {
            customerId = await customerLookupClient.GetCustomerIdAsync(accessToken, cancellationToken);
        }
        catch (ServiceUnavailableException)
        {
            // The customer's province is down: the request is queued and the profile is looked up when it is back.
            customerId = currentUser.CustomerId ?? Guid.Empty;
        }

        return Ok(await accountService.RequestAccountApplicationAsync(currentUser.UserId, customerId, accountType, purpose, identityDocument!, identityBackDocument!, addressDocument, signature!, fileStore, cancellationToken));
    }

    [Authorize]
    [HttpGet("get-account-applications")]
    public async Task<ActionResult<IReadOnlyList<AccountApplicationResponse>>> GetAccountApplications(CancellationToken cancellationToken)
    {
        return Ok(await accountService.GetAccountApplicationsAsync(currentUser.UserId, currentUser.CanViewBankRecords, cancellationToken));
    }

    [Authorize(Roles = "Admin,InternalEmployee")]
    [HttpPost("update-account-application-status/{applicationId:guid}")]
    public async Task<ActionResult<AccountApplicationResponse>> UpdateAccountApplicationStatus(Guid applicationId, [FromBody] ReviewApplicationRequest? request, CancellationToken cancellationToken)
    {
        var status = request?.Status ?? string.Empty;
        if (status is not (ApplicationStatuses.Pending or ApplicationStatuses.Approved or ApplicationStatuses.Rejected)) throw new ValidationException("Account application status is invalid.");
        
        try
        {
            return Ok(await accountService.UpdateAccountApplicationStatusAsync(applicationId, status, request?.Note ?? string.Empty, cancellationToken));
        }
        catch (Exception exception) when (RegionErrors.IsRegionDown(exception))
        {
            // The customer's province is down (master and slave): RabbitMQ keeps the decision until it is back.
            await eventPublisher.PublishAsync(new AccountApplicationReviewQueuedEvent { ApplicationId = applicationId, Status = status, Note = request?.Note ?? string.Empty }, cancellationToken);
            return Accepted(new { queued = true, message = "This customer's province database is down right now. Your decision is saved and will be applied automatically when it is back; the customer will be notified." });
        }
    }

    [Authorize]
    [HttpGet("get-accounts")]
    public async Task<ActionResult<IReadOnlyList<AccountResponse>>> GetAccounts(CancellationToken cancellationToken)
    {
        return Ok(await accountService.GetAccountsAsync(currentUser.UserId, currentUser.CanViewBankRecords, cancellationToken));
    }

    // Staff list with paging: search (account number, type) and status filter over every province.
    [Authorize(Roles = "Admin,InternalEmployee,ExternalEmployee")]
    [HttpGet("search-accounts")]
    public async Task<ActionResult<PagedResult<AccountResponse>>> SearchAccounts([FromQuery] string? search, [FromQuery] string? status, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken cancellationToken)
    {
        return Ok(await accountService.SearchAccountsAsync(search, status, page, pageSize, cancellationToken));
    }

    [Authorize]
    [HttpGet("get-account-by-id/{accountId:guid}")]
    public async Task<ActionResult<AccountResponse>> GetAccountById(Guid accountId, CancellationToken cancellationToken)
    {
        return Ok(await accountService.GetAccountByIdAsync(accountId, currentUser.UserId, currentUser.CanViewBankRecords, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{accountId:guid}/freeze")]
    public async Task<ActionResult<AccountResponse>> Freeze(Guid accountId, CancellationToken cancellationToken)
    {
        var status = AccountStatuses.Frozen;
        if (status is not (AccountStatuses.Frozen or AccountStatuses.Active or AccountStatuses.Closed)) throw new ValidationException("Account status is invalid.");
        
        return Ok(await accountService.UpdateAccountStatusAsync(accountId, status, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{accountId:guid}/unfreeze")]
    public async Task<ActionResult<AccountResponse>> Unfreeze(Guid accountId, CancellationToken cancellationToken)
    {
        var status = AccountStatuses.Active;
        if (status is not (AccountStatuses.Frozen or AccountStatuses.Active or AccountStatuses.Closed)) throw new ValidationException("Account status is invalid.");
        return Ok(await accountService.UpdateAccountStatusAsync(accountId, status, cancellationToken));
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{accountId:guid}/close")]
    public async Task<ActionResult<AccountResponse>> Close(Guid accountId, CancellationToken cancellationToken)
    {
        var status = AccountStatuses.Closed;
        if (status is not (AccountStatuses.Frozen or AccountStatuses.Active or AccountStatuses.Closed)) throw new ValidationException("Account status is invalid.");
        return Ok(await accountService.UpdateAccountStatusAsync(accountId, status, cancellationToken));
    }

    [InternalApiKey]
    [HttpGet("get-internal-employee-account-by-id/{accountId:guid}")]
    public async Task<ActionResult<AccountResponse>> GetInternalEmployeeAccountById(Guid accountId, CancellationToken cancellationToken)
    {
        return Ok(await accountService.GetInternalEmployeeAccountByIdAsync(accountId, cancellationToken));
    }

    [InternalApiKey]
    [HttpGet("get-account-by-account-number/{accountNumber}")]
    public async Task<ActionResult<AccountResponse>> GetAccountByAccountNumber(string accountNumber, CancellationToken cancellationToken)
    {
        return Ok(await accountService.GetAccountByAccountNumberAsync(accountNumber, cancellationToken));
    }

    // A customer's account ids, so their history can include deposits made by staff and incoming transfers.
    [InternalApiKey]
    [HttpGet("internal/users/{userId:guid}/account-ids")]
    public async Task<ActionResult<IReadOnlyList<Guid>>> AccountIdsOfUser(Guid userId, CancellationToken cancellationToken)
    {
        var accounts = await accountService.GetAccountsAsync(userId, false, cancellationToken);
        return Ok(accounts.Select(account => account.Id).ToList());
    }

    [InternalApiKey]
    [HttpGet("internal/{accountId:guid}/movements/{referenceNumber}")]
    public async Task<IActionResult> MovementApplied(Guid accountId, string referenceNumber, [FromQuery] string side, CancellationToken cancellationToken)
    {
        var applied = await accountService.MovementAppliedAsync(accountId, referenceNumber, string.Equals(side, "debit", StringComparison.OrdinalIgnoreCase), cancellationToken);
        return Ok(new { applied });
    }

    [InternalApiKey]
    [HttpPost("internal/{accountId:guid}/debit")]
    public async Task<IActionResult> Debit(Guid accountId, MoneyMovementRequest request, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0) throw new ValidationException("Amount must be greater than zero.");
        await accountService.DebitAsync(accountId, request, cancellationToken);
        return NoContent();
    }

    [InternalApiKey]
    [HttpPost("internal/{accountId:guid}/credit")]
    public async Task<IActionResult> Credit(Guid accountId, MoneyMovementRequest request, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0) throw new ValidationException("Amount must be greater than zero.");
        await accountService.CreditAsync(accountId, request, cancellationToken);
        return NoContent();
    }

    // Two-phase commit participant (the coordinator is the transaction service).
    [InternalApiKey]
    [HttpPost("internal/2pc/prepare")]
    public async Task<ActionResult<TwoPhaseVoteResponse>> PrepareTwoPhase(TwoPhasePrepareRequest request, [FromServices] TwoPhaseParticipant participant, CancellationToken cancellationToken)
    {
        return Ok(await participant.PrepareAsync(request, cancellationToken));
    }

    [InternalApiKey]
    [HttpPost("internal/2pc/commit")]
    public async Task<ActionResult<TwoPhaseAckResponse>> CommitTwoPhase(TwoPhaseDecisionRequest request, [FromServices] TwoPhaseParticipant participant, CancellationToken cancellationToken)
    {
        return Ok(await participant.CommitAsync(request, cancellationToken));
    }

    [InternalApiKey]
    [HttpPost("internal/2pc/abort")]
    public async Task<ActionResult<TwoPhaseAckResponse>> AbortTwoPhase(TwoPhaseDecisionRequest request, [FromServices] TwoPhaseParticipant participant, CancellationToken cancellationToken)
    {
        return Ok(await participant.AbortAsync(request, cancellationToken));
    }

    [InternalApiKey]
    [HttpGet("internal/2pc/in-doubt")]
    public async Task<ActionResult<IReadOnlyList<TwoPhaseInDoubt>>> InDoubtTwoPhase([FromServices] TwoPhaseParticipant participant, CancellationToken cancellationToken)
    {
        return Ok(await participant.InDoubtAsync(cancellationToken));
    }
}
