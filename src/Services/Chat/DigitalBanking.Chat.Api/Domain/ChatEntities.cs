namespace DigitalBanking.Chat.Api.Domain;

public static class ConversationTypes
{
    public const string Direct = "Direct";
    public const string Group = "Group";
    public const string Support = "Support";
}

public static class MessageTypes
{
    public const string Text = "Text";
    public const string File = "File";
    public const string Voice = "Voice";
    public const string Call = "Call";
    public const string System = "System";
}

public static class CallStatuses
{
    public const string Ringing = "Ringing";
    public const string Active = "Active";
    public const string Ended = "Ended";
    public const string Missed = "Missed";
    public const string Declined = "Declined";
}

public sealed class ChatConversation
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ConversationType { get; set; } = ConversationTypes.Direct;
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<ChatMember> Members { get; set; } = [];
    public List<ChatMessage> Messages { get; set; } = [];
}

public sealed class ChatMember
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime JoinedAtUtc { get; set; }
    public DateTime? LastReadAtUtc { get; set; }
    public ChatConversation Conversation { get; set; } = null!;
}

public sealed class ChatMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderUserId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string MessageType { get; set; } = MessageTypes.Text;
    public string Body { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? FileUrl { get; set; }
    public string? ContentType { get; set; }
    public int? DurationSeconds { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public ChatConversation Conversation { get; set; } = null!;
}

public sealed class ChatCall
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid StartedByUserId { get; set; }
    public string CallType { get; set; } = "Voice";
    public string Status { get; set; } = CallStatuses.Ringing;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
}
