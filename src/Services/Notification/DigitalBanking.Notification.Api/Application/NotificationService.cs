using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Notification.Api.Domain;
using DigitalBanking.Notification.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Notification.Api.Application;

public sealed class NotificationService(NotificationDbContext dbContext)
{
    public async Task<IReadOnlyList<UserNotification>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await dbContext.Notifications
            .Where(notification => notification.UserId == userId)
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    public async Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken)
    {
        var notification = await dbContext.Notifications.FirstOrDefaultAsync(item => item.Id == notificationId, cancellationToken)
            ?? throw new NotFoundException("Notification was not found.");

        if (notification.UserId != userId)
        {
            throw new ForbiddenException("You cannot update this notification.");
        }

        notification.IsRead = true;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var unreadNotifications = await dbContext.Notifications
            .Where(notification => notification.UserId == userId && !notification.IsRead)
            .ToListAsync(cancellationToken);

        foreach (var notification in unreadNotifications)
        {
            notification.IsRead = true;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public void QueueForUser(Guid userId, string title, string body)
    {
        var channels = new[] { NotificationChannels.InApp, NotificationChannels.Email, NotificationChannels.Sms };
        foreach (var channel in channels)
        {
            var notification = new UserNotification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Channel = channel,
                Title = title,
                Body = body,
                CreatedAtUtc = DateTime.UtcNow
            };
            dbContext.Notifications.Add(notification);
            OutboxWriter.AddEvent(dbContext, new NotificationSentEvent
            {
                NotificationId = notification.Id,
                UserId = userId,
                Channel = channel,
                Title = title
            });
        }
    }
}
