using System.Text.Json;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.Chat.Api.Hubs;
using DigitalBanking.Contracts.Events;
using Microsoft.AspNetCore.SignalR;

namespace DigitalBanking.Chat.Api.Application;

// Pushes every new in-app notification (transfer received, account opened, ...) to the user's open app
// over the chat hub, so balances and alerts update without a page refresh.
public sealed class NotificationPushHandler(IHubContext<ChatHub> hubContext) : IIntegrationEventHandler
{
    public async Task HandleAsync(string payload, CancellationToken cancellationToken)
    {
        var notification = JsonSerializer.Deserialize<NotificationSentEvent>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        // The Notification service saves each alert for InApp, Email and Sms; push it once.
        if (notification is null || !string.Equals(notification.Channel, "InApp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await hubContext.Clients.Group($"user:{notification.UserId}").SendAsync("NotificationReceived", new
        {
            notification.NotificationId,
            notification.Title,
            notification.Body
        }, cancellationToken);
    }
}
