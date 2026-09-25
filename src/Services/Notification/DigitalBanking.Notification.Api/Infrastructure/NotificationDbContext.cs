using DigitalBanking.BuildingBlocks.Inbox;
using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.Notification.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Notification.Api.Infrastructure;

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options)
    : DbContext(options), IInboxDbContext, IOutboxDbContext
{
    public DbSet<UserNotification> Notifications => Set<UserNotification>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserNotification>(entity =>
        {
            entity.HasKey(notification => notification.Id);
            entity.Property(notification => notification.Channel).HasMaxLength(16).IsRequired();
            entity.Property(notification => notification.Title).HasMaxLength(200).IsRequired();
            entity.Property(notification => notification.Body).HasMaxLength(1000).IsRequired();
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.HasKey(message => message.EventId);
            entity.Property(message => message.EventType).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.EventType).HasMaxLength(128).IsRequired();
            entity.Property(message => message.Payload).IsRequired();
        });
    }
}
