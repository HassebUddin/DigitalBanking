using System.Text.Json;
using DigitalBanking.BuildingBlocks.Inbox;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Customer.Api.Infrastructure;

namespace DigitalBanking.Customer.Api.Application;

public sealed class CustomerRegisteredHandler(CustomerDbContext dbContext, CustomerService customerService) : IIntegrationEventHandler
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

        if (!await InboxGuard.TryBeginAsync(dbContext, registeredEvent.EventId, nameof(CustomerRegisteredEvent), cancellationToken))
        {
            return;
        }

        await customerService.CreateCustomerFromRegistrationAsync(registeredEvent.UserId, registeredEvent.Email, registeredEvent.FullName, registeredEvent.NationalId, registeredEvent.PhoneNumber, registeredEvent.Address, cancellationToken);

        InboxGuard.MarkProcessed(dbContext, registeredEvent.EventId);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
