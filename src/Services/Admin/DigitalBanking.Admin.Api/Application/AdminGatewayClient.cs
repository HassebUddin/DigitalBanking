using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace DigitalBanking.Admin.Api.Application;

public sealed class AdminGatewayClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    public Task<IReadOnlyList<CustomerRow>> GetCustomersAsync(string accessToken, CancellationToken cancellationToken)
    {
        return GetListAsync<CustomerRow>(configuration["ServiceEndpoints:Customer"]!, "/api/customers/get-customers", accessToken, cancellationToken);
    }

    public Task<IReadOnlyList<AccountRow>> GetAccountsAsync(string accessToken, CancellationToken cancellationToken)
    {
        return GetListAsync<AccountRow>(configuration["ServiceEndpoints:Account"]!, "/api/accounts/get-accounts", accessToken, cancellationToken);
    }

    public Task<IReadOnlyList<TransactionRow>> GetTransactionsAsync(string accessToken, CancellationToken cancellationToken)
    {
        return GetListAsync<TransactionRow>(configuration["ServiceEndpoints:Transaction"]!, "/api/transactions/get-transactions", accessToken, cancellationToken);
    }

    public Task<IReadOnlyList<AuditRow>> GetAuditLogsAsync(string accessToken, CancellationToken cancellationToken)
    {
        return GetListAsync<AuditRow>(configuration["ServiceEndpoints:Audit"]!, "/api/audit-logs/get-audit-list", accessToken, cancellationToken);
    }

    public async Task VerifyKycAsync(string accessToken, Guid customerId, CancellationToken cancellationToken)
    {
        await PostAsync(configuration["ServiceEndpoints:Customer"]!, $"/api/customers/update-kyc-status/{customerId}", accessToken, cancellationToken);
    }

    public async Task FreezeAccountAsync(string accessToken, Guid accountId, CancellationToken cancellationToken)
    {
        await PostAsync(configuration["ServiceEndpoints:Account"]!, $"/api/accounts/{accountId}/freeze", accessToken, cancellationToken);
    }

    public async Task UnfreezeAccountAsync(string accessToken, Guid accountId, CancellationToken cancellationToken)
    {
        await PostAsync(configuration["ServiceEndpoints:Account"]!, $"/api/accounts/{accountId}/unfreeze", accessToken, cancellationToken);
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string baseAddress, string path, string accessToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseAddress, accessToken);
        var items = await client.GetFromJsonAsync<List<T>>(path, cancellationToken);
        return items ?? [];
    }

    private async Task PostAsync(string baseAddress, string path, string accessToken, CancellationToken cancellationToken)
    {
        using var client = CreateClient(baseAddress, accessToken);
        var response = await client.PostAsync(path, null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpClient CreateClient(string baseAddress, string accessToken)
    {
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(baseAddress);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}

public sealed class CustomerRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NationalId { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string KycStatus { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class AccountRow
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class TransactionRow
{
    public Guid Id { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class AuditRow
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Payload { get; set; } = string.Empty;
}

public sealed class DashboardResponse
{
    public int CustomerCount { get; set; }
    public int AccountCount { get; set; }
    public int TransactionCount { get; set; }
    public decimal TotalBalances { get; set; }
}
