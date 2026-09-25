using DigitalBanking.Admin.Api.Application;
using DigitalBanking.BuildingBlocks.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "admin-service");
builder.Services.AddHttpClient();
builder.Services.AddScoped<AdminGatewayClient>();

var app = builder.Build();
app.UseBankingServicePipeline();
app.Run();
