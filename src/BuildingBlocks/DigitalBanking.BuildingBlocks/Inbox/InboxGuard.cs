using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.BuildingBlocks.Inbox;

public static class InboxGuard
{
    public static async Task<bool> TryBeginAsync(IInboxDbContext dbContext, Guid eventId, string eventType, CancellationToken cancellationToken)
    {
        var alreadyProcessed = await dbContext.InboxMessages.AnyAsync(message => message.EventId == eventId, cancellationToken);
        if (alreadyProcessed)
        {
            return false;
        }

        dbContext.InboxMessages.Add(new InboxMessage
        {
            EventId = eventId,
            EventType = eventType,
            ReceivedAtUtc = DateTime.UtcNow
        });

        return true;
    }

    public static void MarkProcessed(IInboxDbContext dbContext, Guid eventId)
    {
        var inboxMessage = dbContext.InboxMessages.Local.FirstOrDefault(message => message.EventId == eventId);
        if (inboxMessage is not null)
        {
            inboxMessage.ProcessedAtUtc = DateTime.UtcNow;
        }
    }
}
