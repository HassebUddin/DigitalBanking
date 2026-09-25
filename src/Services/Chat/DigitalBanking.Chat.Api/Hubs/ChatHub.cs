using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.Chat.Api.Application;
using DigitalBanking.Chat.Api.Contracts;
using DigitalBanking.Chat.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DigitalBanking.Chat.Api.Hubs;

[Authorize]
public sealed class ChatHub(ChatService chatService, PresenceTracker presenceTracker, CurrentUser currentUser) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var cameOnline = presenceTracker.Connect(currentUser.UserId, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(currentUser.UserId));
        if (cameOnline)
        {
            await Clients.All.SendAsync("UserPresenceChanged", new PresenceNotification
            {
                UserId = currentUser.UserId,
                IsOnline = true
            });
        }

        await Clients.Caller.SendAsync("OnlineUsers", presenceTracker.OnlineUserIds());
        await BroadcastReceiptsAsync(await chatService.MarkIncomingDeliveredAsync(currentUser.UserId, Context.ConnectionAborted));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var wentOffline = presenceTracker.Disconnect(currentUser.UserId, Context.ConnectionId);
        if (wentOffline)
        {
            await Clients.All.SendAsync("UserPresenceChanged", new PresenceNotification
            {
                UserId = currentUser.UserId,
                IsOnline = false
            });
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinConversation(Guid conversationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroup(conversationId));
        await BroadcastReceiptsAsync(await chatService.MarkConversationReadAsync(currentUser.UserId, conversationId, Context.ConnectionAborted));
    }

    public async Task AcknowledgeRead(Guid conversationId)
    {
        await BroadcastReceiptsAsync(await chatService.MarkConversationReadAsync(currentUser.UserId, conversationId, Context.ConnectionAborted));
    }

    public async Task SendText(Guid conversationId, string body)
    {
        var message = await chatService.SendTextAsync(currentUser.UserId, currentUser.FullName, conversationId, body, Context.ConnectionAborted);
        await BroadcastMessageAsync(conversationId, message);
    }

    public async Task NotifyTyping(Guid conversationId)
    {
        await Clients.OthersInGroup(ConversationGroup(conversationId)).SendAsync("UserTyping", new TypingNotification
        {
            ConversationId = conversationId,
            UserId = currentUser.UserId,
            DisplayName = currentUser.FullName
        });
    }

    public async Task StartCall(Guid conversationId, string callType)
    {
        var call = await chatService.StartCallAsync(currentUser.UserId, currentUser.FullName, conversationId, callType, Context.ConnectionAborted);
        await Clients.Group(ConversationGroup(conversationId)).SendAsync("IncomingCall", call);
    }

    public async Task AcceptCall(Guid callId)
    {
        var call = await chatService.UpdateCallAsync(currentUser.UserId, callId, CallStatuses.Active, Context.ConnectionAborted);
        await Clients.Group(ConversationGroup(call.ConversationId)).SendAsync("CallAccepted", call);
    }

    public async Task DeclineCall(Guid callId)
    {
        var call = await chatService.UpdateCallAsync(currentUser.UserId, callId, CallStatuses.Declined, Context.ConnectionAborted);
        await Clients.Group(ConversationGroup(call.ConversationId)).SendAsync("CallEnded", call);
    }

    public async Task EndCall(Guid callId)
    {
        var call = await chatService.UpdateCallAsync(currentUser.UserId, callId, CallStatuses.Ended, Context.ConnectionAborted);
        await Clients.Group(ConversationGroup(call.ConversationId)).SendAsync("CallEnded", call);
    }

    public async Task SendOffer(Guid callId, string sdp)
    {
        var call = await chatService.GetCallAsync(callId, currentUser.UserId, Context.ConnectionAborted);
        await Clients.OthersInGroup(ConversationGroup(call.ConversationId)).SendAsync("CallOffer", new { callId, sdp, fromUserId = currentUser.UserId });
    }

    public async Task SendAnswer(Guid callId, string sdp)
    {
        var call = await chatService.GetCallAsync(callId, currentUser.UserId, Context.ConnectionAborted);
        await Clients.OthersInGroup(ConversationGroup(call.ConversationId)).SendAsync("CallAnswer", new { callId, sdp, fromUserId = currentUser.UserId });
    }

    public async Task SendIceCandidate(Guid callId, string candidate)
    {
        var call = await chatService.GetCallAsync(callId, currentUser.UserId, Context.ConnectionAborted);
        await Clients.OthersInGroup(ConversationGroup(call.ConversationId)).SendAsync("CallIceCandidate", new { callId, candidate, fromUserId = currentUser.UserId });
    }

    private async Task BroadcastMessageAsync(Guid conversationId, MessageResponse message)
    {
        await Clients.Group(ConversationGroup(conversationId)).SendAsync("MessageReceived", message);
        foreach (var userId in await chatService.GetMemberUserIdsAsync(conversationId, Context.ConnectionAborted))
        {
            await Clients.Group(UserGroup(userId)).SendAsync("MessageReceived", message);
        }
    }

    private async Task BroadcastReceiptsAsync(IReadOnlyList<MessageReceiptNotification> receipts)
    {
        foreach (var group in receipts.GroupBy(receipt => receipt.ConversationId))
        {
            await Clients.Group(ConversationGroup(group.Key)).SendAsync("MessageReceipts", group.ToList());
        }

        foreach (var senderUserId in receipts.Select(receipt => receipt.SenderUserId).Distinct())
        {
            await Clients.Group(UserGroup(senderUserId)).SendAsync("MessageReceipts", receipts.Where(receipt => receipt.SenderUserId == senderUserId).ToList());
        }
    }

    private static string ConversationGroup(Guid conversationId) => $"conversation:{conversationId}";
    private static string UserGroup(Guid userId) => $"user:{userId}";
}
