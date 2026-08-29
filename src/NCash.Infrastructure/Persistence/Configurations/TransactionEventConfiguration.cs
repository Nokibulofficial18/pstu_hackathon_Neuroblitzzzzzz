using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCash.Domain.Entities;

namespace NCash.Infrastructure.Persistence.Configurations;

public class TransactionEventConfiguration : IEntityTypeConfiguration<TransactionEvent>
{
    public void Configure(EntityTypeBuilder<TransactionEvent> builder)
    {
        builder.ToTable("transaction_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.TransactionId)
            .HasColumnName("transaction_id")
            .IsRequired();

        builder.Property(e => e.EventType)
            .HasColumnName("event_type")
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(e => e.Description)
            .HasColumnName("description")
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(e => e.MetadataJson)
            .HasColumnName("metadata")
            .HasColumnType("jsonb");

        builder.Property(e => e.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne(e => e.Transaction)
            .WithMany(t => t.Events)
            .HasForeignKey(e => e.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Required indexes for event trace
        builder.HasIndex(e => e.TransactionId);
        builder.HasIndex(e => e.EventType);
        builder.HasIndex(e => e.CreatedAtUtc);
    }
}
