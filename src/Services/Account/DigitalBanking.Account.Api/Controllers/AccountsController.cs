using DigitalBanking.Account.Api.Application;
using DigitalBanking.Account.Api.Contracts;
using DigitalBanking.Account.Api.Domain;
using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.BuildingBlocks.Exceptions;
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
    [HttpPost("add-account")]
    public async Task<ActionResult<AccountResponse>> AddAccount(OpenAccountRequest request, CancellationToken cancellationToken)
    {
        var accessToken = Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty);
        var customerId = await customerLookupClient.GetCustomerIdAsync(accessToken, cancellationToken);
    
        var account = await accountService.AddAccountAsync(currentUser.UserId, customerId, request, cancellationToken);
        return Ok(account);
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
        
        var customerId = await customerLookupClient.GetCustomerIdAsync(accessToken, cancellationToken);
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
        
        return Ok(await accountService.UpdateAccountApplicationStatusAsync(applicationId, status, request?.Note ?? string.Empty, cancellationToken));
    }

    [Authorize]
    [HttpGet("get-accounts")]
    public async Task<ActionResult<IReadOnlyList<AccountResponse>>> GetAccounts(CancellationToken cancellationToken)
    {
        return Ok(await accountService.GetAccountsAsync(currentUser.UserId, currentUser.CanViewBankRecords, cancellationToken));
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
}
