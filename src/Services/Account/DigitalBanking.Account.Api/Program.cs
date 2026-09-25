using DigitalBanking.Account.Api.Application;
using DigitalBanking.Account.Api.Infrastructure;
using DigitalBanking.BuildingBlocks.Http;
using DigitalBanking.BuildingBlocks.Web;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 25_000_000);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 25_000_000);
builder.Services.AddBankingServiceDefaults(builder.Configuration, "account-service");
builder.Services.AddDbContext<AccountDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Account")));
builder.Services.AddOutboxPublisher<AccountDbContext>();
builder.Services.AddScoped<AccountService>();
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
await app.EnsureDatabaseCreatedAsync<AccountDbContext>();
await EnsureApplicationsTableAsync(app);
app.Run();

static async Task EnsureApplicationsTableAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AccountDbContext>();
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        IF OBJECT_ID(N'dbo.AccountApplications', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.AccountApplications (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                UserId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                AccountType nvarchar(32) NOT NULL,
                Purpose nvarchar(250) NULL,
                IdentityDocumentUrl nvarchar(400) NULL,
                AddressDocumentUrl nvarchar(400) NULL,
                SignatureUrl nvarchar(400) NULL,
                TermsAccepted bit NOT NULL,
                Status nvarchar(32) NOT NULL,
                ReviewNote nvarchar(400) NULL,
                CreatedAccountId uniqueidentifier NULL,
                CreatedAtUtc datetime2 NOT NULL,
                ReviewedAtUtc datetime2 NULL
            );
        END
        """);
}
