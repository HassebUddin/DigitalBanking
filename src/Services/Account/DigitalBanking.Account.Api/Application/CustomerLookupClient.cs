using System.Net.Http.Headers;
using DigitalBanking.BuildingBlocks.Exceptions;

namespace DigitalBanking.Account.Api.Application;

public sealed class CustomerLookupClient(HttpClient httpClient)
{
    public async Task<Guid> GetCustomerIdAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/customers/get-or-create-customer-profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new ServiceUnavailableException("The customer service is not reachable right now.");
        }

        // 5xx (e.g. the customer's province database is down): retryable, not "no profile".
        if ((int)response.StatusCode >= 500)
        {
            throw new ServiceUnavailableException("The customer profile cannot be read right now.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new BusinessRuleException("Customer profile must exist before an account can be opened.");
        }

        var customer = await response.Content.ReadFromJsonAsync<CustomerLookupResponse>(cancellationToken)
            ?? throw new BusinessRuleException("Customer profile could not be read.");
        return customer.Id;
    }

    private sealed class CustomerLookupResponse
    {
        public Guid Id { get; set; }
    }
}
