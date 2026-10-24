using DigitalBanking.Account.Api.Application;
using DigitalBanking.Account.Api.Infrastructure;
using DigitalBanking.Account.Api.Repository;
using DigitalBanking.BuildingBlocks.Http;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.BuildingBlocks.Web;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 25_000_000);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 25_000_000);
builder.Services.AddBankingServiceDefaults(builder.Configuration, "account-service");
builder.Services.AddRegionalDbContext<AccountDbContext>();
builder.Services.AddRegionalOutboxPublisher<AccountDbContext>();
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<LedgerAuditor>();
builder.Services.AddSingleton<AccountDirectory>();
builder.Services.AddSingleton<AccountFileStore>();
builder.Services.Configure<ServiceEndpoints>(builder.Configuration.GetSection(ServiceEndpoints.SectionName));
builder.Services.AddHttpClient<CustomerLookupClient>((serviceProvider, httpClient) =>
{
    var endpoints = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceEndpoints>>().Value;
    httpClient.BaseAddress = new Uri(endpoints.Customer);
});

var app = builder.Build();
app.UseStaticFiles();
app.UseBankingServicePipeline();
await app.EnsureRegionalDatabasesAsync<AccountDbContext>("DigitalBanking_Account");
await AccountBootstrap.RunAsync(app);
app.Run();

