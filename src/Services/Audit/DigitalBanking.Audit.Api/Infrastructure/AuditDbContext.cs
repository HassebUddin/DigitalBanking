using DigitalBanking.Audit.Api.Domain;
using DigitalBanking.BuildingBlocks.Inbox;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Audit.Api.Infrastructure;

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options), IInboxDbContext
{
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(log => log.Id);
            entity.Property(log => log.EventType).HasMaxLength(128).IsRequired();
            entity.Property(log => log.Payload).IsRequired();
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.HasKey(message => message.EventId);
            entity.Property(message => message.EventType).HasMaxLength(128).IsRequired();
        });
    }
}
