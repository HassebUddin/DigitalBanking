using DigitalBanking.BuildingBlocks.Http;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.Contracts.Events;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.Transaction.Api.Application;
using DigitalBanking.Transaction.Api.Infrastructure;
using DigitalBanking.Transaction.Api.Repository;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "transaction-service");
builder.Services.AddDbContext<TransactionDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Transaction")));
builder.Services.AddOutboxPublisher<TransactionDbContext>();
builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddHostedService<TransferSagaRecoveryHostedService>();
builder.Services.AddEventHandler<TransferRequestedHandler>(nameof(TransferRequestedEvent));
builder.Services.Configure<ServiceEndpoints>(builder.Configuration.GetSection(ServiceEndpoints.SectionName));
builder.Services.AddHttpClient<AccountLedgerClient>((serviceProvider, httpClient) =>
{
    var endpoints = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceEndpoints>>().Value;
    httpClient.BaseAddress = new Uri(endpoints.Account);
    // Short timeout: a hung account service should defer the transfer, not hold the request open.
    httpClient.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();
app.UseBankingServicePipeline();
await app.EnsureDatabaseCreatedAsync<TransactionDbContext>();
await EnsureDeferredTransferColumnsAsync(app);
app.Run();

static async Task EnsureDeferredTransferColumnsAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<TransactionDbContext>();
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        IF COL_LENGTH(N'dbo.TransferSagas', N'DestinationAccountNumber') IS NULL
            ALTER TABLE dbo.TransferSagas ADD DestinationAccountNumber nvarchar(32) NULL;

        IF COL_LENGTH(N'dbo.TransferSagas', N'RetryAfterUtc') IS NULL
            ALTER TABLE dbo.TransferSagas ADD RetryAfterUtc datetime2 NULL;
        """);
}
