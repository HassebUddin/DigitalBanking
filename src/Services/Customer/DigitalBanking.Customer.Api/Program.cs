using DigitalBanking.BuildingBlocks.Messaging;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.Contracts.Events;
using DigitalBanking.Customer.Api.Application;
using DigitalBanking.Customer.Api.Infrastructure;
using DigitalBanking.Customer.Api.Repository;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "customer-service");
builder.Services.AddDbContext<CustomerDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Customer")));
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddEventHandler<CustomerRegisteredHandler>(nameof(CustomerRegisteredEvent));

var app = builder.Build();
app.UseBankingServicePipeline();
await app.EnsureDatabaseCreatedAsync<CustomerDbContext>();
app.Run();
