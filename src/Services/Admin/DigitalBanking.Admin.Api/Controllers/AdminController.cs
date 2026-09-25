using DigitalBanking.Admin.Api.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DigitalBanking.Admin.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin")]
public sealed class AdminController(AdminGatewayClient adminGatewayClient) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardResponse>> Dashboard(CancellationToken cancellationToken)
    {
        var accessToken = ReadAccessToken();
        var customers = await adminGatewayClient.GetCustomersAsync(accessToken, cancellationToken);
        var accounts = await adminGatewayClient.GetAccountsAsync(accessToken, cancellationToken);
        var transactions = await adminGatewayClient.GetTransactionsAsync(accessToken, cancellationToken);

        return Ok(new DashboardResponse
        {
            CustomerCount = customers.Count,
            AccountCount = accounts.Count,
            TransactionCount = transactions.Count,
            TotalBalances = accounts.Sum(account => account.Balance)
        });
    }

    [HttpGet("customers")]
    public async Task<ActionResult<IReadOnlyList<CustomerRow>>> Customers(CancellationToken cancellationToken)
    {
        return Ok(await adminGatewayClient.GetCustomersAsync(ReadAccessToken(), cancellationToken));
    }

    [HttpGet("accounts")]
    public async Task<ActionResult<IReadOnlyList<AccountRow>>> Accounts(CancellationToken cancellationToken)
    {
        return Ok(await adminGatewayClient.GetAccountsAsync(ReadAccessToken(), cancellationToken));
    }

    [HttpGet("transactions")]
    public async Task<ActionResult<IReadOnlyList<TransactionRow>>> Transactions(CancellationToken cancellationToken)
    {
        return Ok(await adminGatewayClient.GetTransactionsAsync(ReadAccessToken(), cancellationToken));
    }

    [HttpGet("audit-logs")]
    public async Task<ActionResult<IReadOnlyList<AuditRow>>> AuditLogs(CancellationToken cancellationToken)
    {
        return Ok(await adminGatewayClient.GetAuditLogsAsync(ReadAccessToken(), cancellationToken));
    }

    [HttpPost("accounts/{accountId:guid}/freeze")]
    public async Task<IActionResult> Freeze(Guid accountId, CancellationToken cancellationToken)
    {
        await adminGatewayClient.FreezeAccountAsync(ReadAccessToken(), accountId, cancellationToken);
        return NoContent();
    }

    [HttpPost("accounts/{accountId:guid}/unfreeze")]
    public async Task<IActionResult> Unfreeze(Guid accountId, CancellationToken cancellationToken)
    {
        await adminGatewayClient.UnfreezeAccountAsync(ReadAccessToken(), accountId, cancellationToken);
        return NoContent();
    }

    private string ReadAccessToken()
    {
        return Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty);
    }
}
