using DigitalBanking.BuildingBlocks.Http;
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
builder.Services.Configure<ServiceEndpoints>(builder.Configuration.GetSection(ServiceEndpoints.SectionName));
builder.Services.AddHttpClient<AccountLedgerClient>((serviceProvider, httpClient) =>
{
    var endpoints = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceEndpoints>>().Value;
    httpClient.BaseAddress = new Uri(endpoints.Account);
});

var app = builder.Build();
app.UseBankingServicePipeline();
await app.EnsureDatabaseCreatedAsync<TransactionDbContext>();
app.Run();
