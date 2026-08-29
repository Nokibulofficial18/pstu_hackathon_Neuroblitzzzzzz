using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCash.Domain.Entities;

namespace NCash.Infrastructure.Persistence.Configurations;

public class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", t =>
        {
            t.HasCheckConstraint("CK_Account_NonNegativeBalance", "\"balance\" >= 0");
        });

        builder.HasKey(a => a.Id);

        builder.Property(a => a.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        // 1 wallet per user constraint
        builder.HasIndex(a => a.UserId)
            .IsUnique();

        builder.Property(a => a.AccountNumber)
            .HasColumnName("account_number")
            .IsRequired()
            .HasMaxLength(30);

        builder.HasIndex(a => a.AccountNumber)
            .IsUnique();

        builder.Property(a => a.Balance)
            .HasColumnName("balance")
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(a => a.Currency)
            .HasColumnName("currency")
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(a => a.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(a => a.Version)
            .HasColumnName("version")
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(a => a.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(a => a.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .IsRequired();
    }
}
