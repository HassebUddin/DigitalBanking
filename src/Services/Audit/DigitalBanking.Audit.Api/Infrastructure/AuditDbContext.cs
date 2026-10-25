using DigitalBanking.Audit.Api.Domain;
using DigitalBanking.BuildingBlocks.Inbox;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Audit.Api.Infrastructure;

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options), IInboxDbContext
{
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<PreparedAuditEntry> PreparedEntries => Set<PreparedAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Lives in each province database (DigitalBanking_Punjab_A, ...) next to the other banking services.
        modelBuilder.HasDefaultSchema("audit");

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(log => log.Id);
            entity.Property(log => log.EventType).HasMaxLength(128).IsRequired();
            entity.Property(log => log.Payload).IsRequired();
        });

        // Two-phase-commit participant log (added to existing databases by DatabaseBootstrap).
        modelBuilder.Entity<PreparedAuditEntry>(entity =>
        {
            entity.ToTable("PreparedEntries");
            entity.HasKey(entry => entry.TransactionId);
            entity.Property(entry => entry.EventType).HasMaxLength(128).IsRequired();
            entity.Property(entry => entry.Payload).IsRequired();
            entity.Property(entry => entry.State).HasMaxLength(16).IsRequired();
            entity.Property(entry => entry.Reason).HasMaxLength(200);
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.HasKey(message => message.EventId);
            entity.Property(message => message.EventType).HasMaxLength(128).IsRequired();
        });
    }
}
