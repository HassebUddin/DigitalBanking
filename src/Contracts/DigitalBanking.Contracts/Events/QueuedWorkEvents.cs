namespace DigitalBanking.Contracts.Events;

// Work accepted while a province's databases (master and slave) were both down. The message waits in RabbitMQ
// and the account service retries it every few seconds until the province is back.

// A customer's account request; the documents are already saved, only the database row is missing.
public sealed record AccountApplicationQueuedEvent : IntegrationEvent
{
    public Guid ApplicationId { get; init; }
    public Guid UserId { get; init; }
    public Guid CustomerId { get; init; }
    public string AccountType { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public string IdentityDocumentUrl { get; init; } = string.Empty;
    public string IdentityBackDocumentUrl { get; init; } = string.Empty;
    public string AddressDocumentUrl { get; init; } = string.Empty;
    public string SignatureUrl { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
}

// A staff decision (approve / reject / back to review) on an account request.
public sealed record AccountApplicationReviewQueuedEvent : IntegrationEvent
{
    public Guid ApplicationId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
}

// Any service can ask the notification service to tell a customer something
// (e.g. "your queued transfer could not be completed").
public sealed record CustomerNotificationRequestedEvent : IntegrationEvent
{
    public Guid UserId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
}
