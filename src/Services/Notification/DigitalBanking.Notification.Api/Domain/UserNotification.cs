namespace DigitalBanking.Notification.Api.Domain;

public sealed class UserNotification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public static class NotificationChannels
{
    public const string Email = "Email";
    public const string Sms = "Sms";
    public const string InApp = "InApp";
}
