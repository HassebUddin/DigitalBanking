using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.BuildingBlocks.Exceptions;
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
    [HttpPost("deposit-transaction")]
    public async Task<ActionResult<TransactionResponse>> DepositTransaction(DepositRequest request, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0) throw new ValidationException("Amount must be greater than zero.");
        return Ok(await transactionService.DepositTransactionAsync(currentUser.UserId, currentUser.IsAdmin, request, cancellationToken));
    }

    [HttpPost("withdraw-transaction")]
    public async Task<ActionResult<TransactionResponse>> WithdrawTransaction(WithdrawRequest request, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0) throw new ValidationException("Amount must be greater than zero.");
        return Ok(await transactionService.WithdrawTransactionAsync(currentUser.UserId, currentUser.IsAdmin, request, cancellationToken));
    }

    [HttpPost("transfer-transaction")]
    public async Task<ActionResult<TransactionResponse>> TransferTransaction(TransferRequest request, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0) throw new ValidationException("Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(request.DestinationAccountNumber)) throw new ValidationException("Destination account number is required.");
        return Ok(await transactionService.TransferTransactionAsync(currentUser.UserId, currentUser.IsAdmin, request, cancellationToken));
    }

    [HttpGet("get-transactions")]
    public async Task<ActionResult<IReadOnlyList<TransactionResponse>>> GetTransactions([FromQuery] string? searchText, [FromQuery] decimal? minimumAmount, [FromQuery] decimal? maximumAmount, [FromQuery] string? transactionType, [FromQuery] DateTime? fromDateUtc, [FromQuery] DateTime? toDateUtc, [FromQuery] string sortBy = "createdAt", [FromQuery] string sortDirection = "desc", CancellationToken cancellationToken = default)
    {
        var request = new TransactionSearchRequest { SearchText = searchText, MinimumAmount = minimumAmount, MaximumAmount = maximumAmount, TransactionType = transactionType, FromDateUtc = fromDateUtc, ToDateUtc = toDateUtc, SortBy = sortBy, SortDirection = sortDirection };
        return Ok(await transactionService.GetTransactionsAsync(currentUser.UserId, currentUser.CanViewBankRecords, request, cancellationToken));
    }

    [HttpGet("get-transaction-statement/{accountId:guid}")]
    public async Task<ActionResult<StatementResponse>> GetTransactionStatement(Guid accountId, [FromQuery] DateTime fromDateUtc, [FromQuery] DateTime toDateUtc, CancellationToken cancellationToken)
    {
        return Ok(await transactionService.GetTransactionStatementAsync(currentUser.UserId, currentUser.CanViewBankRecords, accountId, fromDateUtc, toDateUtc, cancellationToken));
    }
}
