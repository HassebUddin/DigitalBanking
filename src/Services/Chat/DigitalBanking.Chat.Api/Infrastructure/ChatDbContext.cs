using DigitalBanking.Chat.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DigitalBanking.Chat.Api.Infrastructure;

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<ChatConversation> Conversations => Set<ChatConversation>();
    public DbSet<ChatMember> Members => Set<ChatMember>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<ChatCall> Calls => Set<ChatCall>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatConversation>(entity =>
        {
            entity.HasKey(conversation => conversation.Id);
            entity.Property(conversation => conversation.Title).HasMaxLength(200);
            entity.Property(conversation => conversation.ConversationType).HasMaxLength(32).IsRequired();
        });

        modelBuilder.Entity<ChatMember>(entity =>
        {
            entity.HasKey(member => member.Id);
            entity.HasIndex(member => new { member.ConversationId, member.UserId }).IsUnique();
            entity.Property(member => member.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(member => member.Role).HasMaxLength(32).IsRequired();
            entity.HasOne(member => member.Conversation)
                .WithMany(conversation => conversation.Members)
                .HasForeignKey(member => member.ConversationId);
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.SenderName).HasMaxLength(200).IsRequired();
            entity.Property(message => message.MessageType).HasMaxLength(32).IsRequired();
            entity.Property(message => message.Body).HasMaxLength(4000);
            entity.Property(message => message.FileName).HasMaxLength(260);
            entity.Property(message => message.FileUrl).HasMaxLength(500);
            entity.Property(message => message.ContentType).HasMaxLength(120);
            entity.HasOne(message => message.Conversation)
                .WithMany(conversation => conversation.Messages)
                .HasForeignKey(message => message.ConversationId);
        });

        modelBuilder.Entity<ChatCall>(entity =>
        {
            entity.HasKey(call => call.Id);
            entity.Property(call => call.CallType).HasMaxLength(16).IsRequired();
            entity.Property(call => call.Status).HasMaxLength(16).IsRequired();
        });
    }
}
