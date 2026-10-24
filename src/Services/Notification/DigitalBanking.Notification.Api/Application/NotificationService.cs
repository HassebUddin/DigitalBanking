using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Notification.Api.Domain;
using DigitalBanking.Notification.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Notification.Api.Application;

// A user's notifications live in their province's database.
public sealed class NotificationService(RegionalDb<NotificationDbContext> db)
{
    private readonly List<(Guid UserId, string Title, string Body)> queued = [];

    public async Task<IReadOnlyList<UserNotification>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var dbContext = await db.ForUserAsync(userId, cancellationToken);
        return await dbContext.Notifications
            .Where(notification => notification.UserId == userId && notification.Channel == NotificationChannels.InApp)
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    public async Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken)
    {
        var dbContext = await db.ForUserAsync(userId, cancellationToken);
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
        var dbContext = await db.ForUserAsync(userId, cancellationToken);
        var unreadNotifications = await dbContext.Notifications
            .Where(notification => notification.UserId == userId && !notification.IsRead)
            .ToListAsync(cancellationToken);

        foreach (var notification in unreadNotifications)
        {
            notification.IsRead = true;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Collected while an event is handled, then written to each user's province by FlushAsync
    // (one transfer can notify a sender in Sindh and a receiver in Punjab).
    public void QueueForUser(Guid userId, string title, string body)
    {
        queued.Add((userId, title, body));
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        // One record per notification (shown in the app). Email/SMS copies made every alert appear three times.
        var channels = new[] { NotificationChannels.InApp };
        foreach (var (userId, title, body) in queued)
        {
            var dbContext = await db.ForUserRegionAsync(userId, cancellationToken);
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
                    Title = title,
                    Body = body
                });
            }
        }

        queued.Clear();
    }
}
