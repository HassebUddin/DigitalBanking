using System.Text.Json;
using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts.Events;

namespace DigitalBanking.Account.Api.Application;

// Account work accepted while a province's databases (master and slave) were both down.
// RabbitMQ keeps each message in the account-service queue; while the province is still down the handler waits
// a few seconds and hands it back, so it is retried until the province is up again.
public static class QueuedAccountWork
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static T? Read<T>(string payload) => JsonSerializer.Deserialize<T>(payload, JsonOptions);

    public static async Task RetryWhileProvinceDownAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        try
        {
            await work();
        }
        catch (Exception exception) when (RegionErrors.IsRegionDown(exception))
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            throw;
        }
    }
}

// A customer's account request: saved once the province is back, then the customer is notified.
public sealed class AccountApplicationQueuedHandler(AccountService accountService) : IIntegrationEventHandler
{
    public Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        var queued = QueuedAccountWork.Read<AccountApplicationQueuedEvent>(payload);
        return queued is null
            ? Task.CompletedTask
            : QueuedAccountWork.RetryWhileProvinceDownAsync(() => accountService.ApplyQueuedApplicationAsync(queued, cancellationToken), cancellationToken);
    }
}

// A staff approval/rejection: applied once the province is back. Approving opens the account and the
// customer gets the usual "Account opened" notification.
public sealed class AccountApplicationReviewQueuedHandler(AccountService accountService, ILogger<AccountApplicationReviewQueuedHandler> logger) : IIntegrationEventHandler
{
    public Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        var review = QueuedAccountWork.Read<AccountApplicationReviewQueuedEvent>(payload);
        if (review is null)
        {
            return Task.CompletedTask;
        }

        return QueuedAccountWork.RetryWhileProvinceDownAsync(async () =>
        {
            try
            {
                await accountService.UpdateAccountApplicationStatusAsync(review.ApplicationId, review.Status, review.Note, cancellationToken);
            }
            catch (DomainException exception) when (!RegionErrors.IsRegionDown(exception))
            {
                // E.g. already approved by someone else in the meantime: nothing left to do.
                logger.LogWarning("Queued review of request {ApplicationId} dropped: {Message}", review.ApplicationId, exception.Message);
            }
        }, cancellationToken);
    }
}
