using DigitalBanking.BuildingBlocks.Authentication;
using DigitalBanking.Chat.Api.Application;
using DigitalBanking.Chat.Api.Contracts;
using DigitalBanking.Chat.Api.Domain;
using DigitalBanking.Chat.Api.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace DigitalBanking.Chat.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/chat")]
public sealed class ChatController(
    ChatService chatService,
    ChatFileStore chatFileStore,
    CurrentUser currentUser,
    IHubContext<ChatHub> hubContext) : ControllerBase
{
    [HttpGet("active-users-except")]
    public async Task<ActionResult<IReadOnlyList<DirectoryUserResponse>>> GetActiveUsersExcept(CancellationToken cancellationToken)
    {
        return Ok(await chatService.GetDirectoryAsync(ReadAccessToken(), cancellationToken));
    }

    [HttpGet("conversations")]
    public async Task<ActionResult<IReadOnlyList<ConversationResponse>>> Conversations(CancellationToken cancellationToken)
    {
        return Ok(await chatService.ListConversationsAsync(currentUser.UserId, cancellationToken));
    }

    [HttpPost("conversations/direct")]
    public async Task<ActionResult<ConversationResponse>> Direct(StartDirectChatRequest request, CancellationToken cancellationToken)
    {
        return Ok(await chatService.StartDirectAsync(currentUser.UserId, currentUser.FullName, currentUser.Role, ReadAccessToken(), request, cancellationToken));
    }

    [HttpPost("conversations/group")]
    public async Task<ActionResult<ConversationResponse>> Group(StartGroupChatRequest request, CancellationToken cancellationToken)
    {
        return Ok(await chatService.StartGroupAsync(currentUser.UserId, currentUser.FullName, currentUser.Role, ReadAccessToken(), request, cancellationToken));
    }

    [HttpPost("conversations/support")]
    public async Task<ActionResult<ConversationResponse>> Support(CancellationToken cancellationToken)
    {
        return Ok(await chatService.StartSupportAsync(currentUser.UserId, currentUser.FullName, currentUser.Role, ReadAccessToken(), cancellationToken));
    }

    [HttpGet("conversations/{conversationId:guid}/messages")]
    public async Task<ActionResult<IReadOnlyList<MessageResponse>>> Messages(Guid conversationId, CancellationToken cancellationToken)
    {
        var opened = await chatService.ListMessagesAsync(currentUser.UserId, conversationId, cancellationToken);
        if (opened.Receipts.Count > 0)
        {
            await hubContext.Clients.Group($"conversation:{conversationId}").SendAsync("MessageReceipts", opened.Receipts, cancellationToken);
            foreach (var senderUserId in opened.Receipts.Select(receipt => receipt.SenderUserId).Distinct())
            {
                await hubContext.Clients.Group($"user:{senderUserId}").SendAsync(
                    "MessageReceipts",
                    opened.Receipts.Where(receipt => receipt.SenderUserId == senderUserId).ToList(),
                    cancellationToken);
            }
        }

        return Ok(opened.Messages);
    }

    [HttpPost("conversations/{conversationId:guid}/files")]
    public async Task<ActionResult<MessageResponse>> UploadFile(Guid conversationId, IFormFile file, CancellationToken cancellationToken)
    {
        var stored = await chatFileStore.SaveAsync(file, "files", cancellationToken);
        var message = await chatService.SendFileAsync(currentUser.UserId, currentUser.FullName, conversationId, stored.fileName, stored.fileUrl, stored.contentType, MessageTypes.File, null, cancellationToken);
        await BroadcastMessageAsync(conversationId, message, cancellationToken);
        return Ok(message);
    }

    [HttpPost("conversations/{conversationId:guid}/voice")]
    public async Task<ActionResult<MessageResponse>> UploadVoice(Guid conversationId, IFormFile file, [FromForm] int? durationSeconds, CancellationToken cancellationToken)
    {
        var stored = await chatFileStore.SaveAsync(file, "voice", cancellationToken);
        var message = await chatService.SendFileAsync(currentUser.UserId, currentUser.FullName, conversationId, stored.fileName, stored.fileUrl, stored.contentType, MessageTypes.Voice, durationSeconds, cancellationToken);
        await BroadcastMessageAsync(conversationId, message, cancellationToken);
        return Ok(message);
    }

    private async Task BroadcastMessageAsync(Guid conversationId, MessageResponse message, CancellationToken cancellationToken)
    {
        await hubContext.Clients.Group($"conversation:{conversationId}").SendAsync("MessageReceived", message, cancellationToken);
        foreach (var userId in await chatService.GetMemberUserIdsAsync(conversationId, cancellationToken))
        {
            await hubContext.Clients.Group($"user:{userId}").SendAsync("MessageReceived", message, cancellationToken);
        }
    }

    private string ReadAccessToken()
    {
        return Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty);
    }
}
