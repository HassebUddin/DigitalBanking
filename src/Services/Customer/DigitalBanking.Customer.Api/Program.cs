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
app.Run();
