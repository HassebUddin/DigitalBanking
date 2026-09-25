using System.Text.Json;
using DigitalBanking.BuildingBlocks.Inbox;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.Contracts;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Notification.Api.Infrastructure;

namespace DigitalBanking.Notification.Api.Application;

public abstract class NotificationEventHandler<TEvent>(NotificationDbContext dbContext, NotificationService notificationService)
    : IIntegrationEventHandler
    where TEvent : IntegrationEvent
{
    public async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        var integrationEvent = JsonSerializer.Deserialize<TEvent>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (integrationEvent is null)
        {
            return;
        }

        if (!await InboxGuard.TryBeginAsync(dbContext, integrationEvent.EventId, typeof(TEvent).Name, cancellationToken))
        {
            return;
        }

        HandleEvent(integrationEvent);
        InboxGuard.MarkProcessed(dbContext, integrationEvent.EventId);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    protected abstract void HandleEvent(TEvent integrationEvent);

    protected void Notify(Guid userId, string title, string body)
    {
        notificationService.QueueForUser(userId, title, body);
    }
}

public sealed class AccountApplicationRejectedNotificationHandler(NotificationDbContext dbContext, NotificationService notificationService)
    : NotificationEventHandler<AccountApplicationRejectedEvent>(dbContext, notificationService)
{
    protected override void HandleEvent(AccountApplicationRejectedEvent integrationEvent)
    {
        var reason = string.IsNullOrWhiteSpace(integrationEvent.Note)
            ? "Please send a new request with clearer documents."
            : integrationEvent.Note;
        Notify(
            integrationEvent.UserId,
            "Account request rejected",
            $"Your {integrationEvent.AccountType} account request was rejected. {reason}");
    }
}

public sealed class AccountApplicationReopenedNotificationHandler(NotificationDbContext dbContext, NotificationService notificationService)
    : NotificationEventHandler<AccountApplicationReopenedEvent>(dbContext, notificationService)
{
    protected override void HandleEvent(AccountApplicationReopenedEvent integrationEvent)
    {
        Notify(
            integrationEvent.UserId,
            "Account request back in review",
            $"Your {integrationEvent.AccountType} account request is being reviewed again.");
    }
}

public sealed class AccountCreatedNotificationHandler(NotificationDbContext dbContext, NotificationService notificationService)
    : NotificationEventHandler<AccountCreatedEvent>(dbContext, notificationService)
{
    protected override void HandleEvent(AccountCreatedEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Account opened", $"Account {integrationEvent.AccountNumber} is ready.");
    }
}

public sealed class MoneyDepositedNotificationHandler(NotificationDbContext dbContext, NotificationService notificationService)
    : NotificationEventHandler<MoneyDepositedEvent>(dbContext, notificationService)
{
    protected override void HandleEvent(MoneyDepositedEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Deposit successful", $"PKR {integrationEvent.Amount:N2} was deposited. Reference {integrationEvent.ReferenceNumber}.");
    }
}

public sealed class MoneyWithdrawnNotificationHandler(NotificationDbContext dbContext, NotificationService notificationService)
    : NotificationEventHandler<MoneyWithdrawnEvent>(dbContext, notificationService)
{
    protected override void HandleEvent(MoneyWithdrawnEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Withdrawal successful", $"PKR {integrationEvent.Amount:N2} was withdrawn. Reference {integrationEvent.ReferenceNumber}.");
    }
}

public sealed class MoneyTransferredNotificationHandler(NotificationDbContext dbContext, NotificationService notificationService)
    : NotificationEventHandler<MoneyTransferredEvent>(dbContext, notificationService)
{
    protected override void HandleEvent(MoneyTransferredEvent integrationEvent)
    {
        Notify(integrationEvent.SourceUserId, "Transfer sent", $"PKR {integrationEvent.Amount:N2} was transferred. Reference {integrationEvent.ReferenceNumber}.");
        Notify(integrationEvent.DestinationUserId, "Transfer received", $"PKR {integrationEvent.Amount:N2} was credited to your account. Reference {integrationEvent.ReferenceNumber}.");
    }
}

public sealed class PasswordChangedNotificationHandler(NotificationDbContext dbContext, NotificationService notificationService)
    : NotificationEventHandler<PasswordChangedEvent>(dbContext, notificationService)
{
    protected override void HandleEvent(PasswordChangedEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Password changed", "Your password was changed successfully.");
    }
}

public sealed class PasswordResetNotificationHandler(NotificationDbContext dbContext, NotificationService notificationService)
    : NotificationEventHandler<PasswordResetRequestedEvent>(dbContext, notificationService)
{
    protected override void HandleEvent(PasswordResetRequestedEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Password reset code", $"Your password reset code is {integrationEvent.ResetCode}.");
    }
}

public sealed class CustomerRegisteredNotificationHandler(NotificationDbContext dbContext, NotificationService notificationService)
    : NotificationEventHandler<CustomerRegisteredEvent>(dbContext, notificationService)
{
    protected override void HandleEvent(CustomerRegisteredEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Welcome to Digital Banking", "Your customer profile is ready. You can now open an account.");
    }
}
