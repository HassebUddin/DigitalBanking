using System.Net.Http.Json;
using System.Text.Json;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Security;

namespace DigitalBanking.Transaction.Api.Application;

public sealed class AccountLedgerClient(HttpClient httpClient, IConfiguration configuration)
{
    public async Task<AccountSnapshot> GetInternalEmployeeAccountByIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"/api/accounts/get-internal-employee-account-by-id/{accountId}");
        var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new NotFoundException("Account was not found.");
        }

        return await response.Content.ReadFromJsonAsync<AccountSnapshot>(cancellationToken)
            ?? throw new NotFoundException("Account was not found.");
    }

    public async Task<AccountSnapshot> GetByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"/api/accounts/get-account-by-account-number/{accountNumber}");
        var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new NotFoundException("Destination account was not found.");
        }

        return await response.Content.ReadFromJsonAsync<AccountSnapshot>(cancellationToken)
            ?? throw new NotFoundException("Destination account was not found.");
    }

    public async Task DebitAsync(Guid accountId, decimal amount, string referenceNumber, CancellationToken cancellationToken)
    {
        await PostMovementAsync(accountId, "debit", amount, referenceNumber, cancellationToken);
    }

    public async Task CreditAsync(Guid accountId, decimal amount, string referenceNumber, CancellationToken cancellationToken)
    {
        await PostMovementAsync(accountId, "credit", amount, referenceNumber, cancellationToken);
    }

    private async Task PostMovementAsync(Guid accountId, string action, decimal amount, string referenceNumber, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"/api/accounts/internal/{accountId}/{action}");
        request.Content = JsonContent.Create(new { amount, referenceNumber });
        var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new BusinessRuleException(ReadErrorMessage(body));
    }

    private static string ReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "Account movement failed.";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
        }

        return "Account movement failed.";
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(InternalApiKeyAttribute.HeaderName, configuration["InternalApi:Key"]);
        return request;
    }
}

public sealed class AccountSnapshot
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string AccountNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
