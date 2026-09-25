using System.Net.Http.Headers;
using DigitalBanking.BuildingBlocks.Exceptions;

namespace DigitalBanking.Account.Api.Application;

public sealed class CustomerLookupClient(HttpClient httpClient)
{
    public async Task<Guid> GetCustomerIdAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/customers/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await httpClient.SendAsync(request, cancellationToken);
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
