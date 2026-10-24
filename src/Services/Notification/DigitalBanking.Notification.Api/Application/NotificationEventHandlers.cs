using System.Text.Json;
using DigitalBanking.BuildingBlocks.Inbox;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Notification.Api.Infrastructure;

namespace DigitalBanking.Notification.Api.Application;

public abstract class NotificationEventHandler<TEvent>(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
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

        // "Already handled" records are kept in the head office database; the notifications go to each user's province.
        var inbox = db.For(BankRegions.Default);
        if (!await InboxGuard.TryBeginAsync(inbox, integrationEvent.EventId, typeof(TEvent).Name, cancellationToken))
        {
            return;
        }

        try
        {
            HandleEvent(integrationEvent);
            await notificationService.FlushAsync(cancellationToken);
            InboxGuard.MarkProcessed(inbox, integrationEvent.EventId);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (RegionErrors.IsRegionDown(exception))
        {
            // The user's province is down: wait a little, then let RabbitMQ deliver the message again.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            throw;
        }
    }

    protected abstract void HandleEvent(TEvent integrationEvent);

    protected void Notify(Guid userId, string title, string body)
    {
        notificationService.QueueForUser(userId, title, body);
    }
}

public sealed class AccountApplicationRejectedNotificationHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<AccountApplicationRejectedEvent>(db, notificationService)
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

public sealed class AccountApplicationReopenedNotificationHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<AccountApplicationReopenedEvent>(db, notificationService)
{
    protected override void HandleEvent(AccountApplicationReopenedEvent integrationEvent)
    {
        Notify(
            integrationEvent.UserId,
            "Account request back in review",
            $"Your {integrationEvent.AccountType} account request is being reviewed again.");
    }
}

public sealed class AccountCreatedNotificationHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<AccountCreatedEvent>(db, notificationService)
{
    protected override void HandleEvent(AccountCreatedEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Account opened", $"Account {integrationEvent.AccountNumber} is ready.");
    }
}

public sealed class MoneyDepositedNotificationHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<MoneyDepositedEvent>(db, notificationService)
{
    protected override void HandleEvent(MoneyDepositedEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Deposit successful", $"PKR {integrationEvent.Amount:N2} was deposited. Reference {integrationEvent.ReferenceNumber}.");
    }
}

public sealed class MoneyWithdrawnNotificationHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<MoneyWithdrawnEvent>(db, notificationService)
{
    protected override void HandleEvent(MoneyWithdrawnEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Withdrawal successful", $"PKR {integrationEvent.Amount:N2} was withdrawn. Reference {integrationEvent.ReferenceNumber}.");
    }
}

public sealed class MoneyTransferredNotificationHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<MoneyTransferredEvent>(db, notificationService)
{
    protected override void HandleEvent(MoneyTransferredEvent integrationEvent)
    {
        Notify(integrationEvent.SourceUserId, "Transfer sent", $"PKR {integrationEvent.Amount:N2} was transferred. Reference {integrationEvent.ReferenceNumber}.");
        Notify(integrationEvent.DestinationUserId, "Transfer received", $"PKR {integrationEvent.Amount:N2} was credited to your account. Reference {integrationEvent.ReferenceNumber}.");
    }
}

public sealed class TransferFailedNotificationHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<TransferFailedEvent>(db, notificationService)
{
    protected override void HandleEvent(TransferFailedEvent integrationEvent)
    {
        var reason = string.IsNullOrWhiteSpace(integrationEvent.Reason) ? string.Empty : $" {integrationEvent.Reason.TrimEnd('.')}.";
        Notify(integrationEvent.UserId, "Transfer failed", $"Your transfer of PKR {integrationEvent.Amount:N2} could not be completed.{reason} No money was taken. Reference {integrationEvent.ReferenceNumber}.");
    }
}

public sealed class TransferRefundedNotificationHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<TransferRefundedEvent>(db, notificationService)
{
    protected override void HandleEvent(TransferRefundedEvent integrationEvent)
    {
        var reason = string.IsNullOrWhiteSpace(integrationEvent.Reason) ? string.Empty : $" {integrationEvent.Reason.TrimEnd('.')}.";
        Notify(integrationEvent.UserId, "Transfer refunded", $"Your transfer of PKR {integrationEvent.Amount:N2} could not be delivered and the money is back in your account.{reason} Reference {integrationEvent.ReferenceNumber}.");
    }
}

public sealed class PasswordChangedNotificationHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<PasswordChangedEvent>(db, notificationService)
{
    protected override void HandleEvent(PasswordChangedEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Password changed", "Your password was changed successfully.");
    }
}

public sealed class PasswordResetNotificationHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<PasswordResetRequestedEvent>(db, notificationService)
{
    protected override void HandleEvent(PasswordResetRequestedEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Password reset code", $"Your password reset code is {integrationEvent.ResetCode}.");
    }
}

public sealed class CustomerRegisteredNotificationHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<CustomerRegisteredEvent>(db, notificationService)
{
    protected override void HandleEvent(CustomerRegisteredEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, "Welcome to Digital Banking", "Your customer profile is ready. You can now open an account.");
    }
}

// A message another service asked to send (e.g. a queued transfer or account request that could not go through).
public sealed class CustomerNotificationRequestedHandler(RegionalDb<NotificationDbContext> db, NotificationService notificationService)
    : NotificationEventHandler<CustomerNotificationRequestedEvent>(db, notificationService)
{
    protected override void HandleEvent(CustomerNotificationRequestedEvent integrationEvent)
    {
        Notify(integrationEvent.UserId, integrationEvent.Title, integrationEvent.Body);
    }
}
