using DigitalBanking.Account.Api.Domain;
using DigitalBanking.BuildingBlocks.Outbox;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Account.Api.Infrastructure;

public sealed class AccountDbContext(DbContextOptions<AccountDbContext> options) : DbContext(options), IOutboxDbContext
{
    public DbSet<BankAccount> Accounts => Set<BankAccount>();
    public DbSet<AccountApplication> Applications => Set<AccountApplication>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<PreparedMovement> PreparedMovements => Set<PreparedMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Lives in each province database (DigitalBanking_Punjab_A, ...) next to the other banking services.
        modelBuilder.HasDefaultSchema("account");

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
            entity.Property(application => application.IdentityBackDocumentUrl).HasMaxLength(400);
            entity.Property(application => application.AddressDocumentUrl).HasMaxLength(400);
            entity.Property(application => application.SignatureUrl).HasMaxLength(400);
            entity.Property(application => application.Status).HasMaxLength(32).IsRequired();
            entity.Property(application => application.ReviewNote).HasMaxLength(400);
        });

        // Created by AccountBootstrap (the table was added after the other account tables existed).
        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.ToTable("LedgerEntries");
            entity.HasKey(entry => entry.Id);
            entity.HasIndex(entry => new { entry.IdempotencyKey, entry.Leg }).IsUnique();
            entity.HasIndex(entry => entry.AccountId);
            entity.Property(entry => entry.JournalId).HasMaxLength(80).IsRequired();
            entity.Property(entry => entry.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(entry => entry.LedgerAccount).HasMaxLength(40).IsRequired();
            entity.Property(entry => entry.Purpose).HasMaxLength(20).IsRequired();
            entity.Property(entry => entry.Debit).HasPrecision(18, 2);
            entity.Property(entry => entry.Credit).HasPrecision(18, 2);
            entity.Property(entry => entry.BalanceAfter).HasPrecision(18, 2);
        });

        // Two-phase-commit participant log (added to existing databases by DatabaseBootstrap).
        modelBuilder.Entity<PreparedMovement>(entity =>
        {
            entity.ToTable("PreparedMovements");
            entity.HasKey(movement => movement.TransactionId);
            // One in-doubt distributed transaction per account: the "lock" a prepared participant holds.
            entity.HasIndex(movement => movement.AccountId).IsUnique().HasFilter("[State] = N'Prepared'");
            entity.Property(movement => movement.Amount).HasPrecision(18, 2);
            entity.Property(movement => movement.Purpose).HasMaxLength(20).IsRequired();
            entity.Property(movement => movement.ReferenceNumber).HasMaxLength(40).IsRequired();
            entity.Property(movement => movement.State).HasMaxLength(16).IsRequired();
            entity.Property(movement => movement.Reason).HasMaxLength(200);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.EventType).HasMaxLength(128).IsRequired();
            entity.Property(message => message.Payload).IsRequired();
        });
    }
}
