using DigitalBanking.BuildingBlocks.Http;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.Identity.Api.Application;
using DigitalBanking.Identity.Api.Domain;
using DigitalBanking.Identity.Api.Infrastructure;
using DigitalBanking.Identity.Api.Repository;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "identity-service");
builder.Services.AddCentralDbContext<IdentityDbContext>();
builder.Services.AddOutboxPublisher<IdentityDbContext>();
builder.Services.AddScoped<IIdentityRepository, IdentityRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IEmployeeRepository, EmployeeRepository>();
builder.Services.AddSingleton<PasswordHasher<User>>();
builder.Services.AddScoped<AuthService>();
builder.Services.Configure<ServiceEndpoints>(builder.Configuration.GetSection(ServiceEndpoints.SectionName));
builder.Services.AddHttpClient<CustomerProfileClient>((serviceProvider, httpClient) =>
{
    httpClient.BaseAddress = new Uri(serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceEndpoints>>().Value.Customer);
    httpClient.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<EmployeeService>();

var app = builder.Build();
app.UseBankingServicePipeline();
await app.EnsureCentralDatabaseAsync<IdentityDbContext>("DigitalBanking_Identity");

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    var passwordHasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<User>>();
    await IdentitySeeder.SeedAsync(dbContext, passwordHasher);
}

app.Run();
