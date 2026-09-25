namespace DigitalBanking.Chat.Api.Contracts;

public sealed class DirectoryUserResponse
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public sealed class StartDirectChatRequest
{
    public Guid OtherUserId { get; set; }
}

public sealed class StartGroupChatRequest
{
    public string Title { get; set; } = string.Empty;
    public List<Guid> MemberUserIds { get; set; } = [];
}

public sealed class ConversationResponse
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ConversationType { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string LastMessage { get; set; } = string.Empty;
    public DateTime? LastMessageAtUtc { get; set; }
    public int UnreadCount { get; set; }
    public List<MemberResponse> Members { get; set; } = [];
}

public sealed class MemberResponse
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public sealed class MessageResponse
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderUserId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? FileUrl { get; set; }
    public string? ContentType { get; set; }
    public int? DurationSeconds { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class CallResponse
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid StartedByUserId { get; set; }
    public string StartedByName { get; set; } = string.Empty;
    public string CallType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public sealed class TypingNotification
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class PresenceNotification
{
    public Guid UserId { get; set; }
    public bool IsOnline { get; set; }
}
