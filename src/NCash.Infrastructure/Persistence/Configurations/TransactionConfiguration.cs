using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCash.Domain.Entities;

namespace NCash.Infrastructure.Persistence.Configurations;

public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("transactions", t =>
        {
            t.HasCheckConstraint("CK_Transaction_PositiveAmount", "\"amount\" > 0");
            t.HasCheckConstraint("CK_Transaction_DistinctAccounts", "\"sender_account_id\" IS NULL OR \"sender_account_id\" <> \"receiver_account_id\"");
        });

        builder.HasKey(t => t.Id);

        builder.Property(t => t.TransactionNumber)
            .HasColumnName("transaction_number")
            .IsRequired()
            .HasMaxLength(50);

        builder.HasIndex(t => t.TransactionNumber)
            .IsUnique();

        builder.Property(t => t.SenderAccountId)
            .HasColumnName("sender_account_id");

        builder.Property(t => t.ReceiverAccountId)
            .HasColumnName("receiver_account_id")
            .IsRequired();

        builder.Property(t => t.Amount)
            .HasColumnName("amount")
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(t => t.Fee)
            .HasColumnName("fee")
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(t => t.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(t => t.Type)
            .HasColumnName("type")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(t => t.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .IsRequired()
            .HasMaxLength(128);

        builder.HasIndex(t => t.IdempotencyKey)
            .IsUnique();

        builder.Property(t => t.Purpose)
            .HasColumnName("purpose")
            .HasMaxLength(255);

        builder.Property(t => t.RiskScore)
            .HasColumnName("risk_score")
            .IsRequired();

        builder.Property(t => t.RiskLevel)
            .HasColumnName("risk_level")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(t => t.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(500);

        builder.Property(t => t.CompletedAtUtc)
            .HasColumnName("completed_at");

        builder.Property(t => t.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(t => t.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .IsRequired();

        // Foreign keys
        builder.HasOne(t => t.SenderAccount)
            .WithMany()
            .HasForeignKey(t => t.SenderAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.ReceiverAccount)
            .WithMany()
            .HasForeignKey(t => t.ReceiverAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Required indexes for fast paginated queries and balance audits
        builder.HasIndex(t => t.SenderAccountId);
        builder.HasIndex(t => t.ReceiverAccountId);
        builder.HasIndex(t => t.CreatedAtUtc);
        builder.HasIndex(t => new { t.SenderAccountId, t.CreatedAtUtc });
        builder.HasIndex(t => new { t.ReceiverAccountId, t.CreatedAtUtc });
        builder.HasIndex(t => t.Status);
    }
}
