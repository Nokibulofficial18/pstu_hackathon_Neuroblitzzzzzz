using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCash.Domain.Entities;

namespace NCash.Infrastructure.Persistence.Configurations;

public class MoneyRequestConfiguration : IEntityTypeConfiguration<MoneyRequest>
{
    public void Configure(EntityTypeBuilder<MoneyRequest> builder)
    {
        builder.ToTable("money_requests", t =>
        {
            t.HasCheckConstraint("CK_MoneyRequest_PositiveAmount", "\"amount\" > 0");
            t.HasCheckConstraint("CK_MoneyRequest_ValidPaidAmount", "\"paid_amount\" >= 0 AND \"paid_amount\" <= \"amount\"");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.RequesterAccountId)
            .HasColumnName("requester_id")
            .IsRequired();

        builder.Property(r => r.PayerAccountId)
            .HasColumnName("payer_id")
            .IsRequired();

        builder.Property(r => r.Amount)
            .HasColumnName("amount")
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(r => r.PaidAmount)
            .HasColumnName("paid_amount")
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(r => r.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(r => r.Note)
            .HasColumnName("purpose")
            .HasMaxLength(255);

        builder.Property(r => r.ExpiresAtUtc)
            .HasColumnName("expires_at");

        builder.Property(r => r.CompletedAtUtc)
            .HasColumnName("completed_at");

        builder.Property(r => r.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(r => r.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne(r => r.RequesterAccount)
            .WithMany()
            .HasForeignKey(r => r.RequesterAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.PayerAccount)
            .WithMany()
            .HasForeignKey(r => r.PayerAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Required indexes
        builder.HasIndex(r => r.RequesterAccountId);
        builder.HasIndex(r => r.PayerAccountId);
        builder.HasIndex(r => r.Status);
        builder.HasIndex(r => r.CreatedAtUtc);
    }
}
