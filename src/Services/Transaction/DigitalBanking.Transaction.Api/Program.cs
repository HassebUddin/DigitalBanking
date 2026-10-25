using DigitalBanking.BuildingBlocks.Http;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.Contracts.Events;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.Transaction.Api.Application;
using DigitalBanking.Transaction.Api.Infrastructure;
using DigitalBanking.Transaction.Api.Repository;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "transaction-service");
builder.Services.AddRegionalDbContext<TransactionDbContext>();
builder.Services.AddRegionalOutboxPublisher<TransactionDbContext>();
builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddHostedService<TransferSagaRecoveryHostedService>();
// Two-phase commit coordinator (loan disbursements); the participants are the account and audit services.
builder.Services.AddScoped<TwoPhaseCoordinator>();
builder.Services.AddHostedService<TwoPhaseRecoveryHostedService>();
builder.Services.AddHttpClient<TwoPhaseParticipantsClient>(httpClient => httpClient.Timeout = TimeSpan.FromSeconds(15));
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
await app.EnsureRegionalDatabasesAsync<TransactionDbContext>("DigitalBanking_Transaction");
app.Run();

