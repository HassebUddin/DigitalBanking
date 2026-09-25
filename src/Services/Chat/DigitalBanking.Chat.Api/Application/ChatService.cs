using DigitalBanking.BuildingBlocks.Exceptions;
using DigitalBanking.Chat.Api.Contracts;
using DigitalBanking.Chat.Api.Domain;
using DigitalBanking.Chat.Api.Infrastructure;
using DigitalBanking.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Chat.Api.Application;

public sealed class ChatService(ChatDbContext dbContext, DirectoryClient directoryClient)
{
    public Task<IReadOnlyList<DirectoryUserResponse>> GetDirectoryAsync(string accessToken, CancellationToken cancellationToken)
    {
        return directoryClient.ListAsync(accessToken, cancellationToken);
    }

    public async Task<ConversationResponse> StartDirectAsync(Guid userId, string fullName, string role, string accessToken, StartDirectChatRequest request, CancellationToken cancellationToken)
    {
        if (request.OtherUserId == userId)
        {
            throw new ValidationException("You cannot start a chat with yourself.");
        }

        var existing = await dbContext.Conversations
            .Include(conversation => conversation.Members)
            .Include(conversation => conversation.Messages)
            .Where(conversation => conversation.ConversationType == ConversationTypes.Direct)
            .FirstOrDefaultAsync(conversation =>
                conversation.Members.Any(member => member.UserId == userId) &&
                conversation.Members.Any(member => member.UserId == request.OtherUserId), cancellationToken);

        if (existing is not null)
        {
            return MapConversation(existing, userId);
        }

        var otherUser = await directoryClient.GetUserAsync(request.OtherUserId, accessToken, cancellationToken);
        EnsureCanChat(role, otherUser.Role);

        var conversation = CreateConversation(ConversationTypes.Direct, otherUser.FullName, userId);
        AddMember(conversation, userId, fullName, role);
        AddMember(conversation, otherUser.UserId, otherUser.FullName, otherUser.Role);
        dbContext.Conversations.Add(conversation);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapConversation(conversation, userId);
    }

    public async Task<ConversationResponse> StartGroupAsync(Guid userId, string fullName, string role, string accessToken, StartGroupChatRequest request, CancellationToken cancellationToken)
    {
        if (!UserRoles.IsStaff(role))
        {
            throw new ForbiddenException("Only bank staff can create group chats.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ValidationException("Group title is required.");
        }

        var conversation = CreateConversation(ConversationTypes.Group, request.Title.Trim(), userId);
        AddMember(conversation, userId, fullName, role);

        foreach (var memberUserId in request.MemberUserIds.Distinct().Where(id => id != userId))
        {
            var member = await directoryClient.GetUserAsync(memberUserId, accessToken, cancellationToken);
            AddMember(conversation, member.UserId, member.FullName, member.Role);
        }

        if (conversation.Members.Count < 2)
        {
            throw new ValidationException("A group needs at least one other member.");
        }

        dbContext.Conversations.Add(conversation);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapConversation(conversation, userId);
    }

    public async Task<ConversationResponse> StartSupportAsync(Guid userId, string fullName, string role, string accessToken, CancellationToken cancellationToken)
    {
        var existing = await dbContext.Conversations
            .Include(conversation => conversation.Members)
            .Include(conversation => conversation.Messages)
            .Where(conversation => conversation.ConversationType == ConversationTypes.Support)
            .FirstOrDefaultAsync(conversation => conversation.Members.Any(member => member.UserId == userId), cancellationToken);

        if (existing is not null)
        {
            return MapConversation(existing, userId);
        }

        var conversation = CreateConversation(ConversationTypes.Support, "Customer Support", userId);
        AddMember(conversation, userId, fullName, role);

        var directory = await directoryClient.ListAsync(accessToken, cancellationToken);
        foreach (var staff in directory.Where(user => UserRoles.IsStaff(user.Role)).Take(5))
        {
            AddMember(conversation, staff.UserId, staff.FullName, staff.Role);
        }

        dbContext.Conversations.Add(conversation);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapConversation(conversation, userId);
    }

    public async Task<IReadOnlyList<ConversationResponse>> ListConversationsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var conversations = await dbContext.Conversations
            .Include(conversation => conversation.Members)
            .Include(conversation => conversation.Messages)
            .Where(conversation => conversation.Members.Any(member => member.UserId == userId))
            .OrderByDescending(conversation => conversation.Messages.Max(message => (DateTime?)message.CreatedAtUtc) ?? conversation.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return conversations.Select(conversation => MapConversation(conversation, userId)).ToList();
    }

    public async Task<IReadOnlyList<MessageResponse>> ListMessagesAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await GetMembershipAsync(conversationId, userId, cancellationToken);
        var messages = await dbContext.Messages
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.CreatedAtUtc)
            .Take(300)
            .ToListAsync(cancellationToken);

        var member = conversation.Members.First(item => item.UserId == userId);
        member.LastReadAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return messages.Select(MapMessage).ToList();
    }

    public async Task<MessageResponse> SendTextAsync(Guid userId, string fullName, Guid conversationId, string body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ValidationException("Message text is required.");
        }

        return await AddMessageAsync(userId, fullName, conversationId, MessageTypes.Text, body.Trim(), null, null, null, null, cancellationToken);
    }

    public async Task<MessageResponse> SendFileAsync(Guid userId, string fullName, Guid conversationId, string fileName, string fileUrl, string contentType, string messageType, int? durationSeconds, CancellationToken cancellationToken)
    {
        return await AddMessageAsync(userId, fullName, conversationId, messageType, fileName, fileName, fileUrl, contentType, durationSeconds, cancellationToken);
    }

    public async Task<CallResponse> StartCallAsync(Guid userId, string fullName, Guid conversationId, string callType, CancellationToken cancellationToken)
    {
        await GetMembershipAsync(conversationId, userId, cancellationToken);
        var call = new ChatCall
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            StartedByUserId = userId,
            CallType = callType is "Video" ? "Video" : "Voice",
            Status = CallStatuses.Ringing,
            StartedAtUtc = DateTime.UtcNow
        };
        dbContext.Calls.Add(call);
        await AddMessageAsync(userId, fullName, conversationId, MessageTypes.Call, $"{call.CallType} call started", null, null, null, null, cancellationToken);
        return MapCall(call, fullName);
    }

    public async Task<CallResponse> UpdateCallAsync(Guid userId, Guid callId, string status, CancellationToken cancellationToken)
    {
        var call = await dbContext.Calls.FirstOrDefaultAsync(item => item.Id == callId, cancellationToken)
            ?? throw new NotFoundException("Call was not found.");
        await GetMembershipAsync(call.ConversationId, userId, cancellationToken);

        call.Status = status;
        if (status is CallStatuses.Ended or CallStatuses.Missed or CallStatuses.Declined)
        {
            call.EndedAtUtc = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return MapCall(call, string.Empty);
    }

    public async Task<ChatCall> GetCallAsync(Guid callId, Guid userId, CancellationToken cancellationToken)
    {
        var call = await dbContext.Calls.FirstOrDefaultAsync(item => item.Id == callId, cancellationToken)
            ?? throw new NotFoundException("Call was not found.");
        await GetMembershipAsync(call.ConversationId, userId, cancellationToken);
        return call;
    }

    private async Task<MessageResponse> AddMessageAsync(
        Guid userId,
        string fullName,
        Guid conversationId,
        string messageType,
        string body,
        string? fileName,
        string? fileUrl,
        string? contentType,
        int? durationSeconds,
        CancellationToken cancellationToken)
    {
        await GetMembershipAsync(conversationId, userId, cancellationToken);
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderUserId = userId,
            SenderName = fullName,
            MessageType = messageType,
            Body = body,
            FileName = fileName,
            FileUrl = fileUrl,
            ContentType = contentType,
            DurationSeconds = durationSeconds,
            CreatedAtUtc = DateTime.UtcNow
        };
        dbContext.Messages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapMessage(message);
    }

    private async Task<ChatConversation> GetMembershipAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var conversation = await dbContext.Conversations
            .Include(item => item.Members)
            .FirstOrDefaultAsync(item => item.Id == conversationId, cancellationToken)
            ?? throw new NotFoundException("Conversation was not found.");

        if (conversation.Members.All(member => member.UserId != userId))
        {
            throw new ForbiddenException("You are not a member of this conversation.");
        }

        return conversation;
    }

    private static ChatConversation CreateConversation(string type, string title, Guid createdByUserId)
    {
        return new ChatConversation
        {
            Id = Guid.NewGuid(),
            Title = title,
            ConversationType = type,
            CreatedByUserId = createdByUserId,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static void AddMember(ChatConversation conversation, Guid userId, string displayName, string role)
    {
        conversation.Members.Add(new ChatMember
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DisplayName = displayName,
            Role = role,
            JoinedAtUtc = DateTime.UtcNow
        });
    }

    private static void EnsureCanChat(string currentRole, string otherRole)
    {
        if (currentRole == UserRoles.Customer && !UserRoles.IsStaff(otherRole))
        {
            throw new ForbiddenException("Customers can only chat with bank staff.");
        }
    }

    private static ConversationResponse MapConversation(ChatConversation conversation, Guid userId)
    {
        var lastMessage = conversation.Messages.OrderByDescending(message => message.CreatedAtUtc).FirstOrDefault();
        var member = conversation.Members.FirstOrDefault(item => item.UserId == userId);
        var title = conversation.ConversationType == ConversationTypes.Direct
            ? conversation.Members.FirstOrDefault(item => item.UserId != userId)?.DisplayName ?? conversation.Title
            : conversation.Title;

        return new ConversationResponse
        {
            Id = conversation.Id,
            Title = title,
            ConversationType = conversation.ConversationType,
            CreatedAtUtc = conversation.CreatedAtUtc,
            LastMessage = lastMessage?.Body ?? string.Empty,
            LastMessageAtUtc = lastMessage?.CreatedAtUtc,
            UnreadCount = conversation.Messages.Count(message => member?.LastReadAtUtc is null || message.CreatedAtUtc > member.LastReadAtUtc),
            Members = conversation.Members.Select(item => new MemberResponse
            {
                UserId = item.UserId,
                DisplayName = item.DisplayName,
                Role = item.Role
            }).ToList()
        };
    }

    private static MessageResponse MapMessage(ChatMessage message)
    {
        return new MessageResponse
        {
            Id = message.Id,
            ConversationId = message.ConversationId,
            SenderUserId = message.SenderUserId,
            SenderName = message.SenderName,
            MessageType = message.MessageType,
            Body = message.Body,
            FileName = message.FileName,
            FileUrl = message.FileUrl,
            ContentType = message.ContentType,
            DurationSeconds = message.DurationSeconds,
            CreatedAtUtc = message.CreatedAtUtc
        };
    }

    private static CallResponse MapCall(ChatCall call, string startedByName)
    {
        return new CallResponse
        {
            Id = call.Id,
            ConversationId = call.ConversationId,
            StartedByUserId = call.StartedByUserId,
            StartedByName = startedByName,
            CallType = call.CallType,
            Status = call.Status
        };
    }
}
