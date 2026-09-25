using DigitalBanking.BuildingBlocks.Http;
using DigitalBanking.BuildingBlocks.Web;
using DigitalBanking.Chat.Api.Application;
using DigitalBanking.Chat.Api.Hubs;
using DigitalBanking.Chat.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBankingServiceDefaults(builder.Configuration, "chat-service");
builder.Services.AddSignalR();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddSingleton<ChatFileStore>();
builder.Services.AddDbContext<ChatDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Chat")));
builder.Services.Configure<ServiceEndpoints>(builder.Configuration.GetSection(ServiceEndpoints.SectionName));
builder.Services.AddHttpClient<DirectoryClient>((serviceProvider, httpClient) =>
{
    var endpoints = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceEndpoints>>().Value;
    httpClient.BaseAddress = new Uri(endpoints.Identity);
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        policy.WithOrigins("http://localhost:4200", "http://localhost", "capacitor://localhost", "ionic://localhost")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();
app.UseCors("frontend");
app.UseStaticFiles();
app.UseBankingServicePipeline();
app.MapHub<ChatHub>("/hubs/chat");
await app.EnsureDatabaseCreatedAsync<ChatDbContext>();
await EnsureReceiptColumnAsync(app);
app.Run();

static async Task EnsureReceiptColumnAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        IF COL_LENGTH(N'dbo.Messages', N'ReceiptStatus') IS NULL
           AND OBJECT_ID(N'dbo.Messages', N'U') IS NOT NULL
        BEGIN
            ALTER TABLE dbo.Messages ADD ReceiptStatus nvarchar(16) NOT NULL
                CONSTRAINT DF_Messages_ReceiptStatus DEFAULT N'Sent';
        END

        IF COL_LENGTH(N'dbo.ChatMessage', N'ReceiptStatus') IS NULL
           AND OBJECT_ID(N'dbo.ChatMessage', N'U') IS NOT NULL
        BEGIN
            ALTER TABLE dbo.ChatMessage ADD ReceiptStatus nvarchar(16) NOT NULL
                CONSTRAINT DF_ChatMessage_ReceiptStatus DEFAULT N'Sent';
        END
        """);
}
