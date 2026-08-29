using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Domain.Entities;

public class GroupCollectionMember : BaseEntity
{
    public Guid GroupCollectionId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal RequiredAmount { get; private set; }
    public decimal PaidAmount { get; private set; } = 0m;
    public GroupMemberStatus Status { get; private set; } = GroupMemberStatus.Pending;

    // Navigation properties
    public virtual GroupCollection GroupCollection { get; private set; } = null!;
    public virtual User User { get; private set; } = null!;
    public virtual Account Account { get; private set; } = null!;

    private GroupCollectionMember() { } // EF Core

    public GroupCollectionMember(
        Guid groupCollectionId,
        Guid userId,
        Guid accountId,
        decimal requiredAmount)
    {
        if (requiredAmount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Member required amount must be greater than zero.");

        GroupCollectionId = groupCollectionId;
        UserId = userId;
        AccountId = accountId;
        RequiredAmount = requiredAmount;
        PaidAmount = 0m;
        Status = GroupMemberStatus.Pending;
    }

    public decimal RemainingAmount => Math.Max(0m, RequiredAmount - PaidAmount);

    public void RecordPayment(decimal amount)
    {
        if (amount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Payment amount must be positive.");

        if (Status == GroupMemberStatus.Paid || Status == GroupMemberStatus.Declined)
            throw new DomainException(ErrorCodes.InvalidTransactionState, $"Member is already {Status}.");

        PaidAmount += amount;

        if (PaidAmount >= RequiredAmount)
        {
            Status = GroupMemberStatus.Paid;
        }
        else
        {
            Status = GroupMemberStatus.PartiallyPaid;
        }

        Touch();
    }

    public void Decline()
    {
        Status = GroupMemberStatus.Declined;
        Touch();
    }
}
