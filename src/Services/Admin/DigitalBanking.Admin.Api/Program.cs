using DigitalBanking.Admin.Api.Application;
using DigitalBanking.Admin.Api.Infrastructure;
using DigitalBanking.Admin.Api.Ops;
using DigitalBanking.Admin.Api.Ops.Compute;
using DigitalBanking.Admin.Api.Ops.Consensus;
using DigitalBanking.Admin.Api.Ops.Data;
using DigitalBanking.Admin.Api.Ops.Distributed;
using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Contracts.Events;
using DigitalBanking.BuildingBlocks.Web;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "admin-service");
builder.Services.AddHttpClient();
builder.Services.AddScoped<AdminGatewayClient>();

// Admin data (logs, rules, pods, jobs) lives in DigitalBanking_Central next to logins and chat.
builder.Services.AddCentralDbContext<AdminDbContext>();
builder.Services.AddSingleton<TrafficRuleStore>();
builder.Services.AddSingleton<LogStore>();
builder.Services.AddSingleton<LogQueryService>();
builder.Services.AddSingleton<ServiceHealthStore>();
builder.Services.AddSingleton<HostMetrics>();
builder.Services.AddSingleton<DataExplorer>();
builder.Services.AddSingleton<ICustomerSearch, SqlCustomerSearch>();
builder.Services.AddSingleton<BankReplicator>();
builder.Services.AddSingleton<WriteAheadLog>();
AddHostedSingleton<OpsEventLog>(builder.Services);
// Leader election among Admin instances through etcd (Raft); without etcd configured this instance always leads.
AddHostedSingleton<CoordinatorLeadership>(builder.Services);
// Redis global account index, fed from the account service's outbox (AccountCreatedEvent).
builder.Services.AddSingleton<GlobalIndexService>();
builder.Services.AddEventHandler<GlobalIndexProjector>(nameof(AccountCreatedEvent));
// Bloom filters of each province's CNICs, for semi-join customer searches.
AddHostedSingleton<CnicBloomIndex>(builder.Services);
AddHostedSingleton<RegionalDataPlane>(builder.Services);
AddHostedSingleton<PodSupervisor>(builder.Services);
AddHostedSingleton<SimulationEngine>(builder.Services);
AddHostedSingleton<LoadGenerator>(builder.Services);
AddHostedSingleton<DatabaseMonitor>(builder.Services);
AddHostedSingleton<CustomerBatchJobs>(builder.Services);
builder.Services.AddHostedService<LogWriter>();

var app = builder.Build();
app.UseBankingServicePipeline();
// Old ops logs and drill state are not carried over: they are short-lived.
await app.EnsureCentralDatabaseAsync<AdminDbContext>();
await RestoreOperationsStateAsync(app);
app.Run();

static void AddHostedSingleton<T>(IServiceCollection services) where T : class, IHostedService
{
    services.AddSingleton<T>();
    services.AddHostedService(provider => provider.GetRequiredService<T>());
}

static async Task RestoreOperationsStateAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();

    // Jobs that were running when the service stopped can never finish.
    await dbContext.CustomerBatches
        .Where(batch => batch.Status != "Completed" && batch.Status != "Failed" && batch.Status != "Cancelled")
        .ExecuteUpdateAsync(setters => setters.SetProperty(batch => batch.Status, "Interrupted"));

    await app.Services.GetRequiredService<OpsEventLog>().LoadAsync(CancellationToken.None);
    await app.Services.GetRequiredService<RegionalDataPlane>().InitializeAsync(CancellationToken.None);
    await app.Services.GetRequiredService<TrafficRuleStore>().LoadAsync(CancellationToken.None);
}
