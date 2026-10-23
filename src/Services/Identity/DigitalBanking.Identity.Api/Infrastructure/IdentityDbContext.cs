using DigitalBanking.BuildingBlocks.Outbox;
using DigitalBanking.BuildingBlocks.Regions;
using DigitalBanking.Identity.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Identity.Api.Infrastructure;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options), IOutboxDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Lives in DigitalBanking_Central next to chat and admin data.
        modelBuilder.HasDefaultSchema("identity");

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(user => user.Id);
            entity.HasIndex(user => user.Email).IsUnique();
            entity.Property(user => user.Email).HasMaxLength(256).IsRequired();
            entity.Property(user => user.FullName).HasMaxLength(200);
            entity.Property(user => user.PasswordHash).HasMaxLength(500).IsRequired();
            entity.Property(user => user.Role).HasMaxLength(32).IsRequired();
            entity.Property(user => user.Region).HasMaxLength(32).IsRequired().HasDefaultValue(BankRegions.Default);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.Property(token => token.TokenHash).HasMaxLength(128).IsRequired();
            entity.HasOne(token => token.User)
                .WithMany(user => user.RefreshTokens)
                .HasForeignKey(token => token.UserId);
        });

        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.Property(token => token.TokenHash).HasMaxLength(128).IsRequired();
            entity.HasOne(token => token.User)
                .WithMany(user => user.PasswordResetTokens)
                .HasForeignKey(token => token.UserId);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.EventType).HasMaxLength(128).IsRequired();
            entity.Property(message => message.Payload).IsRequired();
        });
    }
}
