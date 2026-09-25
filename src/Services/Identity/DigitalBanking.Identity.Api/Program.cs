using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.Identity.Api.Application;
using DigitalBanking.Identity.Api.Domain;
using DigitalBanking.Identity.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "identity-service");
builder.Services.AddDbContext<IdentityDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Identity")));
builder.Services.AddOutboxPublisher<IdentityDbContext>();
builder.Services.AddSingleton<PasswordHasher<User>>();
builder.Services.AddScoped<AuthService>();

var app = builder.Build();
app.UseBankingServicePipeline();
await app.EnsureDatabaseCreatedAsync<IdentityDbContext>();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    var passwordHasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<User>>();
    await IdentitySeeder.SeedAsync(dbContext, passwordHasher);
}

app.Run();
