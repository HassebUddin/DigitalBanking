using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Customer.Api.Application;
using DigitalBanking.Customer.Api.Infrastructure;
using DigitalBanking.Customer.Api.Repository;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "customer-service");
builder.Services.AddRegionalDbContext<CustomerDbContext>();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddEventHandler<CustomerRegisteredHandler>(nameof(CustomerRegisteredEvent));

var app = builder.Build();
app.UseBankingServicePipeline();
await app.EnsureRegionalDatabasesAsync<CustomerDbContext>("DigitalBanking_Customer");
await EnsureSearchIndexesAsync(app);
app.Run();

// Tables created before the phone/email indexes were added get them here, on every node (master and slave).
static async Task EnsureSearchIndexesAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<RegionalDb<CustomerDbContext>>();
    foreach (var region in BankRegions.All)
    {
        foreach (var node in new[] { 'A', 'B' })
        {
            try
            {
                await using var context = db.ForDatabase(BankRegions.DatabaseName(region, node));
                await context.Database.ExecuteSqlRawAsync("""
                    IF OBJECT_ID(N'customer.Customers') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Customers_PhoneNumber' AND object_id = OBJECT_ID(N'customer.Customers'))
                        CREATE INDEX IX_Customers_PhoneNumber ON customer.Customers (PhoneNumber);
                    IF OBJECT_ID(N'customer.Customers') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Customers_Email' AND object_id = OBJECT_ID(N'customer.Customers'))
                        CREATE INDEX IX_Customers_Email ON customer.Customers (Email);
                    """);
            }
            catch (Exception exception) when (exception is Microsoft.Data.SqlClient.SqlException || RegionErrors.IsUnavailable(exception))
            {
                app.Logger.LogWarning("Search indexes on {Region} {Node} skipped: {Message}", region, node, exception.Message);
            }
        }
    }
}
