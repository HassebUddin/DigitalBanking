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
        var response = await SendAsync(request, cancellationToken);
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
        var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new NotFoundException("Destination account was not found.");
        }

        return await response.Content.ReadFromJsonAsync<AccountSnapshot>(cancellationToken)
            ?? throw new NotFoundException("Destination account was not found.");
    }

    public async Task<IReadOnlyList<Guid>> GetAccountIdsOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"/api/accounts/internal/users/{userId}/account-ids");
        var response = await SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<List<Guid>>(cancellationToken) ?? []
            : [];
    }

    // Whether the account already holds the debit (or credit) made under this reference number.
    public async Task<bool> MovementAppliedAsync(Guid accountId, string referenceNumber, string side, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"/api/accounts/internal/{accountId}/movements/{Uri.EscapeDataString(referenceNumber)}?side={side}");
        var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new NotFoundException("Account was not found.");
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.TryGetProperty("applied", out var applied) && applied.GetBoolean();
    }

    // The reference number is the idempotency key on the account side: retrying a movement never applies it twice.
    // The purpose (Deposit, Withdrawal, TransferOut, TransferIn, Refund) picks the general-ledger side of the posting.
    public async Task DebitAsync(Guid accountId, decimal amount, string referenceNumber, string purpose, CancellationToken cancellationToken)
    {
        await PostMovementAsync(accountId, "debit", amount, referenceNumber, purpose, cancellationToken);
    }

    public async Task CreditAsync(Guid accountId, decimal amount, string referenceNumber, string purpose, CancellationToken cancellationToken)
    {
        await PostMovementAsync(accountId, "credit", amount, referenceNumber, purpose, cancellationToken);
    }

    // A province failing over answers "unavailable" for about ten seconds. Movements are idempotent, so they are
    // simply retried through that window before the caller has to defer the transfer.
    private static readonly TimeSpan[] MovementRetryDelays = [.. Enumerable.Repeat(TimeSpan.FromMilliseconds(1500), 9)];

    private async Task PostMovementAsync(Guid accountId, string action, decimal amount, string referenceNumber, string purpose, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        for (var attempt = 0; ; attempt++)
        {
            using var request = CreateRequest(HttpMethod.Post, $"/api/accounts/internal/{accountId}/{action}");
            request.Content = JsonContent.Create(new { amount, referenceNumber, purpose });
            try
            {
                response = await SendAsync(request, cancellationToken);
                break;
            }
            catch (ServiceUnavailableException) when (attempt < MovementRetryDelays.Length)
            {
                await Task.Delay(MovementRetryDelays[attempt], cancellationToken);
            }
        }

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new BusinessRuleException(ReadErrorMessage(body));
    }

    // Account service down, refusing connections or failing with 5xx -> ServiceUnavailableException,
    // so callers can keep the transfer and retry instead of treating it as a business failure.
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ServiceUnavailableException(AccountServiceDown);
        }

        if ((int)response.StatusCode >= 500)
        {
            response.Dispose();
            throw new ServiceUnavailableException(AccountServiceDown);
        }

        return response;
    }

    private const string AccountServiceDown = "The account service is temporarily unavailable.";

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
