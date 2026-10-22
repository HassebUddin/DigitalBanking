using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Admin.Api.Infrastructure;

// Control-plane database. Customer and transaction data lives in the four regional clusters.

public sealed class CustomerBatch
{
    public int Id { get; set; }
    public string Province { get; set; } = string.Empty;
    public string? City { get; set; }
    public long Requested { get; set; }
    public long Inserted { get; set; }
    public long? FirstId { get; set; }
    public long? LastId { get; set; }
    public string Status { get; set; } = "Queued";
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
}

public sealed class RequestLog
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Service { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? Instance { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Query { get; set; }
    public int StatusCode { get; set; }
    public double DurationMs { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string Region { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? UserAgent { get; set; }
    public bool Blocked { get; set; }
    public string? BlockReason { get; set; }
    public string? TraceId { get; set; }
    public string Source { get; set; } = "real";
}

public sealed class TrafficRule
{
    public long Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int? LimitPerSecond { get; set; }
    public string? Reason { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? RemovedAtUtc { get; set; }
}

public sealed class OpsEvent
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string Severity { get; set; } = "info";
    public string Category { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class ClusterState
{
    public string Province { get; set; } = string.Empty;
    public string PrimaryNode { get; set; } = string.Empty;
    public bool AutoFailover { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class AdminDbContext(DbContextOptions<AdminDbContext> options) : DbContext(options)
{
    public DbSet<CustomerBatch> CustomerBatches => Set<CustomerBatch>();
    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();
    public DbSet<TrafficRule> TrafficRules => Set<TrafficRule>();
    public DbSet<OpsEvent> OpsEvents => Set<OpsEvent>();
    public DbSet<ClusterState> ClusterStates => Set<ClusterState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomerBatch>(entity =>
        {
            entity.Property(batch => batch.Province).HasMaxLength(40);
            entity.Property(batch => batch.City).HasMaxLength(40);
            entity.Property(batch => batch.Status).HasMaxLength(24);
            entity.Property(batch => batch.Error).HasMaxLength(400);
        });

        modelBuilder.Entity<RequestLog>(entity =>
        {
            entity.Property(log => log.Service).HasMaxLength(24).IsUnicode(false);
            entity.Property(log => log.Target).HasMaxLength(24).IsUnicode(false);
            entity.Property(log => log.Instance).HasMaxLength(48).IsUnicode(false);
            entity.Property(log => log.Method).HasMaxLength(8).IsUnicode(false);
            entity.Property(log => log.Path).HasMaxLength(300);
            entity.Property(log => log.Query).HasMaxLength(300);
            entity.Property(log => log.ClientId).HasMaxLength(80);
            entity.Property(log => log.IpAddress).HasMaxLength(45).IsUnicode(false);
            entity.Property(log => log.Region).HasMaxLength(40);
            entity.Property(log => log.UserId).HasMaxLength(64).IsUnicode(false);
            entity.Property(log => log.UserAgent).HasMaxLength(200);
            entity.Property(log => log.BlockReason).HasMaxLength(200);
            entity.Property(log => log.TraceId).HasMaxLength(64).IsUnicode(false);
            entity.Property(log => log.Source).HasMaxLength(8).IsUnicode(false);
            entity.HasIndex(log => log.TimestampUtc);
            entity.HasIndex(log => new { log.Service, log.TimestampUtc });
            entity.HasIndex(log => new { log.StatusCode, log.TimestampUtc });
        });

        modelBuilder.Entity<TrafficRule>(entity =>
        {
            entity.Property(rule => rule.Type).HasMaxLength(32).IsUnicode(false);
            entity.Property(rule => rule.Value).HasMaxLength(200);
            entity.Property(rule => rule.Reason).HasMaxLength(200);
            entity.Property(rule => rule.CreatedBy).HasMaxLength(120);
        });

        modelBuilder.Entity<OpsEvent>(entity =>
        {
            entity.Property(item => item.Severity).HasMaxLength(12).IsUnicode(false);
            entity.Property(item => item.Category).HasMaxLength(24).IsUnicode(false);
            entity.Property(item => item.Source).HasMaxLength(60);
            entity.Property(item => item.Message).HasMaxLength(400);
            entity.HasIndex(item => item.AtUtc);
        });

        modelBuilder.Entity<ClusterState>(entity =>
        {
            entity.HasKey(state => state.Province);
            entity.Property(state => state.Province).HasMaxLength(40);
            entity.Property(state => state.PrimaryNode).HasMaxLength(40);
        });
    }
}
