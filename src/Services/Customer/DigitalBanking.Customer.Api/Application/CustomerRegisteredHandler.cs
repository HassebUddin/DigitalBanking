using System.Text.Json;
using DigitalBanking.BuildingBlocks.Inbox;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Customer.Api.Infrastructure;

namespace DigitalBanking.Customer.Api.Application;

public sealed class CustomerRegisteredHandler(RegionalDb<CustomerDbContext> db, CustomerService customerService) : IIntegrationEventHandler
{
    public async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        var registeredEvent = JsonSerializer.Deserialize<CustomerRegisteredEvent>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (registeredEvent is null)
        {
            return;
        }

        // The profile, and the record that this event was handled, go to the province the customer chose.
        var region = BankRegions.Normalize(registeredEvent.Region);
        var dbContext = db.For(region);
        if (!await InboxGuard.TryBeginAsync(dbContext, registeredEvent.EventId, nameof(CustomerRegisteredEvent), cancellationToken))
        {
            return;
        }

        await customerService.CreateCustomerFromRegistrationAsync(registeredEvent.UserId, registeredEvent.Email, registeredEvent.FullName, registeredEvent.NationalId, registeredEvent.PhoneNumber, registeredEvent.Address, region, cancellationToken);

        InboxGuard.MarkProcessed(dbContext, registeredEvent.EventId);
        await db.SaveChangesAsync(cancellationToken);
    }
}
