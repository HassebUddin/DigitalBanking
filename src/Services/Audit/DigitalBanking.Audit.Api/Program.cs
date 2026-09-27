using DigitalBanking.Audit.Api.Application;
using DigitalBanking.Audit.Api.Infrastructure;
using DigitalBanking.Audit.Api.Repository;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.Contracts.Events;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "audit-service");
builder.Services.AddDbContext<AuditDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Audit")));
builder.Services.AddScoped<IAuditRepository, AuditRepository>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddEventHandler<AuditEventHandler<CustomerRegisteredEvent>>(nameof(CustomerRegisteredEvent));
builder.Services.AddEventHandler<AuditEventHandler<UserLoggedInEvent>>(nameof(UserLoggedInEvent));
builder.Services.AddEventHandler<AuditEventHandler<PasswordChangedEvent>>(nameof(PasswordChangedEvent));
builder.Services.AddEventHandler<AuditEventHandler<AccountCreatedEvent>>(nameof(AccountCreatedEvent));
builder.Services.AddEventHandler<AuditEventHandler<AccountApplicationRejectedEvent>>(nameof(AccountApplicationRejectedEvent));
builder.Services.AddEventHandler<AuditEventHandler<AccountApplicationReopenedEvent>>(nameof(AccountApplicationReopenedEvent));
builder.Services.AddEventHandler<AuditEventHandler<MoneyDepositedEvent>>(nameof(MoneyDepositedEvent));
builder.Services.AddEventHandler<AuditEventHandler<MoneyWithdrawnEvent>>(nameof(MoneyWithdrawnEvent));
builder.Services.AddEventHandler<AuditEventHandler<MoneyTransferredEvent>>(nameof(MoneyTransferredEvent));
builder.Services.AddEventHandler<AuditEventHandler<NotificationSentEvent>>(nameof(NotificationSentEvent));

var app = builder.Build();
app.UseBankingServicePipeline();
await app.EnsureDatabaseCreatedAsync<AuditDbContext>();
app.Run();
