using System.Threading.Channels;
using DigitalBanking.Admin.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Admin.Api.Ops;

public sealed record OpsEventView(long Seq, DateTime AtUtc, string Severity, string Category, string Source, string Message);

/// <summary>Timeline of infrastructure events (failovers, pod crashes, rule changes) shown on the dashboard.</summary>
public sealed class OpsEventLog(IServiceScopeFactory scopeFactory, ILogger<OpsEventLog> logger) : BackgroundService
{
    private readonly LinkedList<OpsEventView> recent = new();
    private readonly Channel<OpsEvent> pending = Channel.CreateUnbounded<OpsEvent>();
    private long sequence;

    public void Info(string category, string source, string message) => Add("info", category, source, message);
    public void Warning(string category, string source, string message) => Add("warning", category, source, message);
    public void Critical(string category, string source, string message) => Add("critical", category, source, message);
    public void Success(string category, string source, string message) => Add("success", category, source, message);

    public IReadOnlyList<OpsEventView> Recent(int take = 40)
    {
        lock (recent)
        {
            return recent.Take(take).ToList();
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
        var stored = await dbContext.OpsEvents.AsNoTracking().OrderByDescending(item => item.Id).Take(60).ToListAsync(cancellationToken);
        lock (recent)
        {
            foreach (var item in stored.OrderBy(item => item.Id))
            {
                recent.AddFirst(new OpsEventView(++sequence, item.AtUtc, item.Severity, item.Category, item.Source, item.Message));
            }
        }
    }

    private void Add(string severity, string category, string source, string message)
    {
        var now = DateTime.UtcNow;
        lock (recent)
        {
            recent.AddFirst(new OpsEventView(++sequence, now, severity, category, source, message));
            while (recent.Count > 200)
            {
                recent.RemoveLast();
            }
        }

        pending.Writer.TryWrite(new OpsEvent { AtUtc = now, Severity = severity, Category = category, Source = source, Message = message.Length > 400 ? message[..400] : message });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in pending.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
                dbContext.OpsEvents.Add(item);
                while (pending.Reader.TryRead(out var next))
                {
                    dbContext.OpsEvents.Add(next);
                }

                await dbContext.SaveChangesAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Persisting ops events failed.");
            }
        }
    }
}
