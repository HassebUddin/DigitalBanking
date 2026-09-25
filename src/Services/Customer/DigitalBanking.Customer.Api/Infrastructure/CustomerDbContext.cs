using DigitalBanking.BuildingBlocks.Inbox;
using DigitalBanking.Customer.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Customer.Api.Infrastructure;

public sealed class CustomerDbContext(DbContextOptions<CustomerDbContext> options) : DbContext(options), IInboxDbContext
{
    public DbSet<CustomerProfile> Customers => Set<CustomerProfile>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomerProfile>(entity =>
        {
            entity.HasKey(customer => customer.Id);
            entity.HasIndex(customer => customer.UserId).IsUnique();
            entity.HasIndex(customer => customer.NationalId).IsUnique();
            entity.Property(customer => customer.FullName).HasMaxLength(200).IsRequired();
            entity.Property(customer => customer.Email).HasMaxLength(256).IsRequired();
            entity.Property(customer => customer.NationalId).HasMaxLength(32).IsRequired();
            entity.Property(customer => customer.PhoneNumber).HasMaxLength(32);
            entity.Property(customer => customer.Address).HasMaxLength(500);
            entity.Property(customer => customer.KycStatus).HasMaxLength(32).IsRequired();
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.HasKey(message => message.EventId);
            entity.Property(message => message.EventType).HasMaxLength(128).IsRequired();
        });
    }
}
