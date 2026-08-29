using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Domain.Entities;

public class GroupCollection : BaseEntity
{
    public Guid CreatorUserId { get; private set; }
    public Guid CreatorAccountId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public decimal TargetAmount { get; private set; }
    public decimal CollectedAmount { get; private set; } = 0m;
    public GroupCollectionStatus Status { get; private set; } = GroupCollectionStatus.Pending;
    public DateTime? ExpiresAtUtc { get; private set; }

    // Navigation properties
    public virtual User CreatorUser { get; private set; } = null!;
    public virtual Account CreatorAccount { get; private set; } = null!;
    public virtual ICollection<GroupCollectionMember> Members { get; private set; } = new List<GroupCollectionMember>();

    private GroupCollection() { } // EF Core

    public GroupCollection(
        Guid creatorUserId,
        Guid creatorAccountId,
        string title,
        string description,
        decimal targetAmount,
        DateTime? expiresAtUtc = null)
    {
        if (targetAmount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Target collection amount must be greater than zero.");

        CreatorUserId = creatorUserId;
        CreatorAccountId = creatorAccountId;
        Title = title.Trim();
        Description = description.Trim();
        TargetAmount = targetAmount;
        CollectedAmount = 0m;
        Status = GroupCollectionStatus.Pending;
        ExpiresAtUtc = expiresAtUtc ?? DateTime.UtcNow.AddDays(14);
    }

    public decimal RemainingAmount => Math.Max(0m, TargetAmount - CollectedAmount);

    public void RecordMemberPayment(decimal amount)
    {
        if (Status != GroupCollectionStatus.Pending && Status != GroupCollectionStatus.PartiallyPaid)
            throw new DomainException(ErrorCodes.InvalidTransactionState, $"Cannot contribute to group collection in status {Status}.");

        if (amount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Contribution amount must be positive.");

        CollectedAmount += amount;

        if (CollectedAmount >= TargetAmount)
        {
            Status = GroupCollectionStatus.Paid;
        }
        else if (CollectedAmount > 0)
        {
            Status = GroupCollectionStatus.PartiallyPaid;
        }

        Touch();
    }

    public void Cancel()
    {
        if (Status == GroupCollectionStatus.Paid)
            throw new DomainException(ErrorCodes.InvalidTransactionState, "Cannot cancel an already completed/paid group collection.");

        Status = GroupCollectionStatus.Cancelled;
        Touch();
    }

    public void CheckExpiration()
    {
        if ((Status == GroupCollectionStatus.Pending || Status == GroupCollectionStatus.PartiallyPaid) &&
            ExpiresAtUtc.HasValue && ExpiresAtUtc.Value < DateTime.UtcNow)
        {
            Status = GroupCollectionStatus.Expired;
            Touch();
        }
    }
}
