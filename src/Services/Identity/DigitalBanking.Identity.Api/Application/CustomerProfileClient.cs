using System.Net.Http.Json;
using DigitalBanking.BuildingBlocks.Security;
using DigitalBanking.Contracts.Events;

namespace DigitalBanking.Identity.Api.Application;

// Hands the registration details (CNIC, phone, address) straight to the customer service, so the customer's
// profile is complete at once even when the message bus is down. Best effort: the CustomerRegistered event in
// the outbox delivers the same details later if this call fails.
public sealed class CustomerProfileClient(HttpClient httpClient, IConfiguration configuration, ILogger<CustomerProfileClient> logger)
{
    // True when another customer already registered this CNIC. If the customer service cannot be asked,
    // registration goes ahead (it then keeps the profile without that CNIC rather than duplicating it).
    public async Task<bool> NationalIdTakenAsync(string nationalId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/customers/internal/national-id-taken?nationalId={Uri.EscapeDataString(nationalId.Trim())}");
            request.Headers.Add(InternalApiKeyAttribute.HeaderName, configuration["InternalApi:Key"]);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);
            return body.TryGetProperty("taken", out var taken) && taken.GetBoolean();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Could not check CNIC with the customer service: {Message}", exception.Message);
            return false;
        }
    }

    // True when the profile was created right away; false when the customer's province is down (master and
    // slave) or the customer service is unreachable: the queued event creates it once the province is back.
    public async Task<bool> SendAsync(CustomerRegisteredEvent registration, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/customers/internal/register")
            {
                Content = JsonContent.Create(new
                {
                    registration.UserId,
                    registration.Email,
                    registration.FullName,
                    registration.NationalId,
                    registration.PhoneNumber,
                    registration.Address,
                    registration.Region
                })
            };
            request.Headers.Add(InternalApiKeyAttribute.HeaderName, configuration["InternalApi:Key"]);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Customer profile for {UserId} not created directly ({Status}); the event will deliver it.", registration.UserId, (int)response.StatusCode);
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Customer service unreachable for {UserId}; the event will deliver the profile: {Message}", registration.UserId, exception.Message);
        }

        return false;
    }
}
