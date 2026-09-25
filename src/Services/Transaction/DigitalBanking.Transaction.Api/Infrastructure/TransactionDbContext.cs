using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.Transaction.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Transaction.Api.Infrastructure;

public sealed class TransactionDbContext(DbContextOptions<TransactionDbContext> options) : DbContext(options), IOutboxDbContext
{
    public DbSet<BankTransaction> Transactions => Set<BankTransaction>();
    public DbSet<TransferSaga> TransferSagas => Set<TransferSaga>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankTransaction>(entity =>
        {
            entity.HasKey(transaction => transaction.Id);
            entity.HasIndex(transaction => transaction.ReferenceNumber).IsUnique();
            entity.Property(transaction => transaction.TransactionType).HasMaxLength(32).IsRequired();
            entity.Property(transaction => transaction.Status).HasMaxLength(32).IsRequired();
            entity.Property(transaction => transaction.ReferenceNumber).HasMaxLength(40).IsRequired();
            entity.Property(transaction => transaction.Description).HasMaxLength(300);
            entity.Property(transaction => transaction.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<TransferSaga>(entity =>
        {
            entity.HasKey(saga => saga.Id);
            entity.Property(saga => saga.State).HasMaxLength(32).IsRequired();
            entity.Property(saga => saga.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.EventType).HasMaxLength(128).IsRequired();
            entity.Property(message => message.Payload).IsRequired();
        });
    }
}
