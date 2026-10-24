using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Notification.Api.Application;
using DigitalBanking.Notification.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "notification-service");
builder.Services.AddRegionalDbContext<NotificationDbContext>();
builder.Services.AddRegionalOutboxPublisher<NotificationDbContext>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddEventHandler<CustomerRegisteredNotificationHandler>(nameof(CustomerRegisteredEvent));
builder.Services.AddEventHandler<AccountCreatedNotificationHandler>(nameof(AccountCreatedEvent));
builder.Services.AddEventHandler<AccountApplicationRejectedNotificationHandler>(nameof(AccountApplicationRejectedEvent));
builder.Services.AddEventHandler<AccountApplicationReopenedNotificationHandler>(nameof(AccountApplicationReopenedEvent));
builder.Services.AddEventHandler<MoneyDepositedNotificationHandler>(nameof(MoneyDepositedEvent));
builder.Services.AddEventHandler<MoneyWithdrawnNotificationHandler>(nameof(MoneyWithdrawnEvent));
builder.Services.AddEventHandler<MoneyTransferredNotificationHandler>(nameof(MoneyTransferredEvent));
builder.Services.AddEventHandler<TransferFailedNotificationHandler>(nameof(TransferFailedEvent));
builder.Services.AddEventHandler<TransferRefundedNotificationHandler>(nameof(TransferRefundedEvent));
builder.Services.AddEventHandler<PasswordChangedNotificationHandler>(nameof(PasswordChangedEvent));
builder.Services.AddEventHandler<PasswordResetNotificationHandler>(nameof(PasswordResetRequestedEvent));

var app = builder.Build();
app.UseBankingServicePipeline();
await app.EnsureRegionalDatabasesAsync<NotificationDbContext>("DigitalBanking_Notification");
app.Run();
