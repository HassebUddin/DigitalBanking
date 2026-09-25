using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Notification.Api.Application;
using DigitalBanking.Notification.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "notification-service");
builder.Services.AddDbContext<NotificationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Notification")));
builder.Services.AddOutboxPublisher<NotificationDbContext>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddEventHandler<CustomerRegisteredNotificationHandler>(nameof(CustomerRegisteredEvent));
builder.Services.AddEventHandler<AccountCreatedNotificationHandler>(nameof(AccountCreatedEvent));
builder.Services.AddEventHandler<AccountApplicationRejectedNotificationHandler>(nameof(AccountApplicationRejectedEvent));
builder.Services.AddEventHandler<AccountApplicationReopenedNotificationHandler>(nameof(AccountApplicationReopenedEvent));
builder.Services.AddEventHandler<MoneyDepositedNotificationHandler>(nameof(MoneyDepositedEvent));
builder.Services.AddEventHandler<MoneyWithdrawnNotificationHandler>(nameof(MoneyWithdrawnEvent));
builder.Services.AddEventHandler<MoneyTransferredNotificationHandler>(nameof(MoneyTransferredEvent));
builder.Services.AddEventHandler<PasswordChangedNotificationHandler>(nameof(PasswordChangedEvent));
builder.Services.AddEventHandler<PasswordResetNotificationHandler>(nameof(PasswordResetRequestedEvent));

var app = builder.Build();
app.UseBankingServicePipeline();
await app.EnsureDatabaseCreatedAsync<NotificationDbContext>();
app.Run();
