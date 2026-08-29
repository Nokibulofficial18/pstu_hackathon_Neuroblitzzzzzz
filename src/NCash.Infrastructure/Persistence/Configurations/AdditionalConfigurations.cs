using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NCash.Domain.Entities;

namespace NCash.Infrastructure.Persistence.Configurations;

public class RiskEventConfiguration : IEntityTypeConfiguration<RiskSignal>
{
    public void Configure(EntityTypeBuilder<RiskSignal> builder)
    {
        builder.ToTable("risk_events");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.TransactionId)
            .HasColumnName("transaction_id")
            .IsRequired();

        builder.Property(r => r.RuleCode)
            .HasColumnName("rule_code")
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(r => r.Reason)
            .HasColumnName("reason")
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(r => r.ScoreImpact)
            .HasColumnName("score")
            .IsRequired();

        builder.Property(r => r.Severity)
            .HasColumnName("severity")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(r => r.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne(r => r.Transaction)
            .WithMany(t => t.RiskSignals)
            .HasForeignKey(r => r.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.TransactionId);
        builder.HasIndex(r => r.RuleCode);
        builder.HasIndex(r => r.CreatedAtUtc);
    }
}

public class AuditEventConfiguration : IEntityTypeConfiguration<SystemAuditLog>
{
    public void Configure(EntityTypeBuilder<SystemAuditLog> builder)
    {
        builder.ToTable("audit_events");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.ActorId)
            .HasColumnName("actor_id");

        builder.Property(a => a.Action)
            .HasColumnName("event_type")
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(a => a.EntityName)
            .HasColumnName("entity_name")
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(a => a.EntityId)
            .HasColumnName("entity_id")
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.MetadataJson)
            .HasColumnName("metadata")
            .HasColumnType("jsonb");

        builder.Property(a => a.OldValueJson)
            .HasColumnName("old_value")
            .HasColumnType("text");

        builder.Property(a => a.NewValueJson)
            .HasColumnName("new_value")
            .HasColumnType("text");

        builder.Property(a => a.IpAddress)
            .HasColumnName("ip_address")
            .HasMaxLength(45);

        builder.Property(a => a.UserAgent)
            .HasColumnName("user_agent")
            .HasMaxLength(255);

        builder.Property(a => a.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasIndex(a => a.EntityId);
        builder.HasIndex(a => a.ActorId);
        builder.HasIndex(a => a.Action);
        builder.HasIndex(a => a.CreatedAtUtc);
    }
}

public class RecoveryCaseConfiguration : IEntityTypeConfiguration<DisputeCase>
{
    public void Configure(EntityTypeBuilder<DisputeCase> builder)
    {
        builder.ToTable("recovery_cases");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.TransactionId)
            .HasColumnName("transaction_id")
            .IsRequired();

        builder.Property(d => d.ReportedByUserId)
            .HasColumnName("reported_by")
            .IsRequired();

        builder.Property(d => d.Category)
            .HasColumnName("issue_type")
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(d => d.Description)
            .HasColumnName("description")
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(d => d.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(d => d.ResolutionNote)
            .HasColumnName("resolution")
            .HasMaxLength(1000);

        builder.Property(d => d.ResolvedAtUtc)
            .HasColumnName("resolved_at");

        builder.Property(d => d.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(d => d.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne(d => d.Transaction)
            .WithMany()
            .HasForeignKey(d => d.TransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.ReportedByUser)
            .WithMany()
            .HasForeignKey(d => d.ReportedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => d.TransactionId);
        builder.HasIndex(d => d.ReportedByUserId);
        builder.HasIndex(d => d.Status);
    }
}

public class GroupCollectionConfiguration : IEntityTypeConfiguration<GroupCollection>
{
    public void Configure(EntityTypeBuilder<GroupCollection> builder)
    {
        builder.ToTable("group_collections", t =>
        {
            t.HasCheckConstraint("CK_GroupCollection_PositiveTarget", "\"target_amount\" > 0");
            t.HasCheckConstraint("CK_GroupCollection_NonNegativeCollected", "\"collected_amount\" >= 0");
        });

        builder.HasKey(g => g.Id);

        builder.Property(g => g.CreatorUserId)
            .HasColumnName("creator_user_id")
            .IsRequired();

        builder.Property(g => g.CreatorAccountId)
            .HasColumnName("creator_account_id")
            .IsRequired();

        builder.Property(g => g.Title)
            .HasColumnName("title")
            .IsRequired()
            .HasMaxLength(150);

        builder.Property(g => g.Description)
            .HasColumnName("description")
            .HasMaxLength(500);

        builder.Property(g => g.TargetAmount)
            .HasColumnName("target_amount")
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(g => g.CollectedAmount)
            .HasColumnName("collected_amount")
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(g => g.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(g => g.ExpiresAtUtc)
            .HasColumnName("expires_at");

        builder.Property(g => g.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(g => g.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne(g => g.CreatorUser)
            .WithMany()
            .HasForeignKey(g => g.CreatorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(g => g.CreatorAccount)
            .WithMany()
            .HasForeignKey(g => g.CreatorAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(g => g.CreatorUserId);
        builder.HasIndex(g => g.Status);
    }
}

public class GroupCollectionMemberConfiguration : IEntityTypeConfiguration<GroupCollectionMember>
{
    public void Configure(EntityTypeBuilder<GroupCollectionMember> builder)
    {
        builder.ToTable("group_collection_members", t =>
        {
            t.HasCheckConstraint("CK_GroupMember_PositiveRequired", "\"required_amount\" > 0");
            t.HasCheckConstraint("CK_GroupMember_NonNegativePaid", "\"paid_amount\" >= 0");
        });

        builder.HasKey(m => m.Id);

        builder.Property(m => m.GroupCollectionId)
            .HasColumnName("group_collection_id")
            .IsRequired();

        builder.Property(m => m.UserId)
            .HasColumnName("user_id")
            .IsRequired();

        builder.Property(m => m.AccountId)
            .HasColumnName("account_id")
            .IsRequired();

        builder.Property(m => m.RequiredAmount)
            .HasColumnName("required_amount")
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(m => m.PaidAmount)
            .HasColumnName("paid_amount")
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(m => m.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(m => m.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(m => m.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne(m => m.GroupCollection)
            .WithMany(g => g.Members)
            .HasForeignKey(m => m.GroupCollectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.Account)
            .WithMany()
            .HasForeignKey(m => m.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => m.GroupCollectionId);
        builder.HasIndex(m => m.UserId);
        builder.HasIndex(m => new { m.GroupCollectionId, m.UserId }).IsUnique();
    }
}

public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Key)
            .HasColumnName("key")
            .IsRequired()
            .HasMaxLength(128);

        builder.HasIndex(i => i.Key)
            .IsUnique();

        builder.Property(i => i.RequestPath)
            .HasColumnName("request_path")
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(i => i.RequestPayloadHash)
            .HasColumnName("request_payload_hash")
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(i => i.ResponseBodyJson)
            .HasColumnName("response_body")
            .HasColumnType("text");

        builder.Property(i => i.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(i => i.ExpiresAtUtc)
            .HasColumnName("expires_at")
            .IsRequired();

        builder.Property(i => i.CreatedAtUtc)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(i => i.UpdatedAtUtc)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(i => i.ExpiresAtUtc);
    }
}
