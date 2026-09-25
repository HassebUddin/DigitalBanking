namespace DigitalBanking.Contracts.Events;

public sealed record CustomerRegisteredEvent : IntegrationEvent
{
    public Guid UserId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string NationalId { get; init; } = string.Empty;
    public string PhoneNumber { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
}

public sealed record UserLoggedInEvent : IntegrationEvent
{
    public Guid UserId { get; init; }
    public string Email { get; init; } = string.Empty;
}

public sealed record PasswordChangedEvent : IntegrationEvent
{
    public Guid UserId { get; init; }
    public string Email { get; init; } = string.Empty;
}

public sealed record PasswordResetRequestedEvent : IntegrationEvent
{
    public Guid UserId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string ResetCode { get; init; } = string.Empty;
}

public sealed record AccountCreatedEvent : IntegrationEvent
{
    public Guid AccountId { get; init; }
    public Guid CustomerId { get; init; }
    public Guid UserId { get; init; }
    public string AccountNumber { get; init; } = string.Empty;
    public string AccountType { get; init; } = string.Empty;
}

public sealed record MoneyDepositedEvent : IntegrationEvent
{
    public Guid TransactionId { get; init; }
    public Guid AccountId { get; init; }
    public Guid UserId { get; init; }
    public decimal Amount { get; init; }
    public string ReferenceNumber { get; init; } = string.Empty;
}

public sealed record MoneyWithdrawnEvent : IntegrationEvent
{
    public Guid TransactionId { get; init; }
    public Guid AccountId { get; init; }
    public Guid UserId { get; init; }
    public decimal Amount { get; init; }
    public string ReferenceNumber { get; init; } = string.Empty;
}

public sealed record MoneyTransferredEvent : IntegrationEvent
{
    public Guid TransactionId { get; init; }
    public Guid SourceAccountId { get; init; }
    public Guid DestinationAccountId { get; init; }
    public Guid SourceUserId { get; init; }
    public Guid DestinationUserId { get; init; }
    public decimal Amount { get; init; }
    public string ReferenceNumber { get; init; } = string.Empty;
}

public sealed record AccountApplicationRejectedEvent : IntegrationEvent
{
    public Guid ApplicationId { get; init; }
    public Guid UserId { get; init; }
    public string AccountType { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
}

public sealed record AccountApplicationReopenedEvent : IntegrationEvent
{
    public Guid ApplicationId { get; init; }
    public Guid UserId { get; init; }
    public string AccountType { get; init; } = string.Empty;
}

public sealed record NotificationSentEvent : IntegrationEvent
{
    public Guid NotificationId { get; init; }
    public Guid UserId { get; init; }
    public string Channel { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}
