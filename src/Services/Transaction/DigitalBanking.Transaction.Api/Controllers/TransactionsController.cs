using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.Transaction.Api.Application;
using DigitalBanking.Transaction.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Transaction.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/transactions")]
public sealed class TransactionsController(TransactionService transactionService, CurrentUser currentUser) : ControllerBase
{
    [HttpPost("deposit")]
    public async Task<ActionResult<TransactionResponse>> Deposit(DepositRequest request, CancellationToken cancellationToken)
    {
        return Ok(await transactionService.DepositAsync(currentUser.UserId, currentUser.IsAdmin, request, cancellationToken));
    }

    [HttpPost("withdraw")]
    public async Task<ActionResult<TransactionResponse>> Withdraw(WithdrawRequest request, CancellationToken cancellationToken)
    {
        return Ok(await transactionService.WithdrawAsync(currentUser.UserId, currentUser.IsAdmin, request, cancellationToken));
    }

    [HttpPost("transfer")]
    public async Task<ActionResult<TransactionResponse>> Transfer(TransferRequest request, CancellationToken cancellationToken)
    {
        return Ok(await transactionService.TransferAsync(currentUser.UserId, currentUser.IsAdmin, request, cancellationToken));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TransactionResponse>>> Search(
        [FromQuery] string? searchText,
        [FromQuery] decimal? minimumAmount,
        [FromQuery] decimal? maximumAmount,
        [FromQuery] string? transactionType,
        [FromQuery] DateTime? fromDateUtc,
        [FromQuery] DateTime? toDateUtc,
        [FromQuery] string sortBy = "createdAt",
        [FromQuery] string sortDirection = "desc",
        CancellationToken cancellationToken = default)
    {
        var request = new TransactionSearchRequest
        {
            SearchText = searchText,
            MinimumAmount = minimumAmount,
            MaximumAmount = maximumAmount,
            TransactionType = transactionType,
            FromDateUtc = fromDateUtc,
            ToDateUtc = toDateUtc,
            SortBy = sortBy,
            SortDirection = sortDirection
        };

        return Ok(await transactionService.SearchAsync(currentUser.UserId, currentUser.CanViewBankRecords, request, cancellationToken));
    }

    [HttpGet("statement/{accountId:guid}")]
    public async Task<ActionResult<StatementResponse>> Statement(
        Guid accountId,
        [FromQuery] DateTime fromDateUtc,
        [FromQuery] DateTime toDateUtc,
        CancellationToken cancellationToken)
    {
        return Ok(await transactionService.GetStatementAsync(currentUser.UserId, currentUser.CanViewBankRecords, accountId, fromDateUtc, toDateUtc, cancellationToken));
    }
}
