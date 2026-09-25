using DigitalBanking.Account.Api.Domain;
using DigitalBanking.BuildingBlocks.Outbox;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Account.Api.Infrastructure;

public sealed class AccountDbContext(DbContextOptions<AccountDbContext> options) : DbContext(options), IOutboxDbContext
{
    public DbSet<BankAccount> Accounts => Set<BankAccount>();
    public DbSet<AccountApplication> Applications => Set<AccountApplication>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankAccount>(entity =>
        {
            entity.HasKey(account => account.Id);
            entity.HasIndex(account => account.AccountNumber).IsUnique();
            entity.Property(account => account.AccountNumber).HasMaxLength(20).IsRequired();
            entity.Property(account => account.AccountType).HasMaxLength(32).IsRequired();
            entity.Property(account => account.Status).HasMaxLength(32).IsRequired();
            entity.Property(account => account.Currency).HasMaxLength(8).IsRequired();
            entity.Property(account => account.Balance).HasPrecision(18, 2);
            entity.Property(account => account.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<AccountApplication>(entity =>
        {
            entity.ToTable("AccountApplications");
            entity.HasKey(application => application.Id);
            entity.Property(application => application.AccountType).HasMaxLength(32).IsRequired();
            entity.Property(application => application.Purpose).HasMaxLength(250);
            entity.Property(application => application.IdentityDocumentUrl).HasMaxLength(400);
            entity.Property(application => application.AddressDocumentUrl).HasMaxLength(400);
            entity.Property(application => application.SignatureUrl).HasMaxLength(400);
            entity.Property(application => application.Status).HasMaxLength(32).IsRequired();
            entity.Property(application => application.ReviewNote).HasMaxLength(400);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.EventType).HasMaxLength(128).IsRequired();
            entity.Property(message => message.Payload).IsRequired();
        });
    }
}
