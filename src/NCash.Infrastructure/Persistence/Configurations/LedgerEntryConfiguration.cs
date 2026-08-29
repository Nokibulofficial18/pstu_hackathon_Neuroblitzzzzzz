using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCash.Domain.Entities;

namespace NCash.Infrastructure.Persistence.Configurations;

public class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("ledger_entries", t =>
        {
            t.HasCheckConstraint("CK_LedgerEntry_PositiveAmount", "\"amount\" > 0");
        });

        builder.HasKey(l => l.Id);

        builder.Property(l => l.TransactionId)
            .HasColumnName("transaction_id")
            .IsRequired();

        builder.Property(l => l.AccountId)
            .HasColumnName("account_id")
            .IsRequired();

        builder.Property(l => l.Direction)
            .HasColumnName("direction")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(l => l.Amount)
            .HasColumnName("amount")
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(l => l.BalanceAfter)
            .HasColumnName("balance_after")
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(l => l.Description)
            .HasColumnName("description")
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(l => l.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();

        // Relationships
        builder.HasOne(l => l.Transaction)
            .WithMany(t => t.LedgerEntries)
            .HasForeignKey(l => l.TransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.Account)
            .WithMany(a => a.LedgerEntries)
            .HasForeignKey(l => l.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Required indexes for double-entry audit and reconciliation
        builder.HasIndex(l => l.TransactionId);
        builder.HasIndex(l => l.AccountId);
        builder.HasIndex(l => l.CreatedAtUtc);
        builder.HasIndex(l => new { l.AccountId, l.CreatedAtUtc });
    }
}
