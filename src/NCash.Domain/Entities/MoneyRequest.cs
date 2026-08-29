using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Domain.Entities;

public class MoneyRequest : BaseEntity
{
    public Guid RequesterAccountId { get; private set; }
    public Guid PayerAccountId { get; private set; }
    public decimal Amount { get; private set; }
    public decimal PaidAmount { get; private set; } = 0m;
    public MoneyRequestStatus Status { get; private set; } = MoneyRequestStatus.Pending;
    public string? Note { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    // Navigation properties
    public virtual Account RequesterAccount { get; private set; } = null!;
    public virtual Account PayerAccount { get; private set; } = null!;

    private MoneyRequest() { } // EF Core

    public MoneyRequest(
        Guid requesterAccountId,
        Guid payerAccountId,
        decimal amount,
        string? note = null,
        DateTime? expiresAtUtc = null)
    {
        if (amount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Requested amount must be greater than zero.");

        if (requesterAccountId == payerAccountId)
            throw new DomainException(ErrorCodes.SelfTransferNotAllowed, "Cannot request money from yourself.");

        RequesterAccountId = requesterAccountId;
        PayerAccountId = payerAccountId;
        Amount = amount;
        PaidAmount = 0m;
        Note = note;
        Status = MoneyRequestStatus.Pending;
        ExpiresAtUtc = expiresAtUtc ?? DateTime.UtcNow.AddDays(7);
    }

    public decimal RemainingAmount => Amount - PaidAmount;

    public void ApplyPayment(decimal paymentAmount)
    {
        if (Status != MoneyRequestStatus.Pending && Status != MoneyRequestStatus.PartiallyPaid)
            throw new DomainException(ErrorCodes.MoneyRequestAlreadyClosed, $"Cannot pay request in state {Status}.");

        if (paymentAmount <= 0)
            throw new DomainException(ErrorCodes.MoneyRequestInvalidAmount, "Payment amount must be positive.");

        if (paymentAmount > RemainingAmount)
            throw new DomainException(ErrorCodes.MoneyRequestInvalidAmount, $"Payment {paymentAmount} exceeds remaining {RemainingAmount}.");

        PaidAmount += paymentAmount;

        if (PaidAmount >= Amount)
        {
            Status = MoneyRequestStatus.Paid;
            CompletedAtUtc = DateTime.UtcNow;
        }
        else
        {
            Status = MoneyRequestStatus.PartiallyPaid;
        }

        Touch();
    }

    public void Reject()
    {
        if (Status != MoneyRequestStatus.Pending && Status != MoneyRequestStatus.PartiallyPaid)
            throw new DomainException(ErrorCodes.MoneyRequestAlreadyClosed, $"Cannot reject request in state {Status}.");

        Status = MoneyRequestStatus.Rejected;
        CompletedAtUtc = DateTime.UtcNow;
        Touch();
    }

    public void Cancel()
    {
        if (Status != MoneyRequestStatus.Pending && Status != MoneyRequestStatus.PartiallyPaid)
            throw new DomainException(ErrorCodes.MoneyRequestAlreadyClosed, $"Cannot cancel request in state {Status}.");

        Status = MoneyRequestStatus.Cancelled;
        CompletedAtUtc = DateTime.UtcNow;
        Touch();
    }

    public void Expire()
    {
        if (Status == MoneyRequestStatus.Pending || Status == MoneyRequestStatus.PartiallyPaid)
        {
            Status = MoneyRequestStatus.Expired;
            CompletedAtUtc = DateTime.UtcNow;
            Touch();
        }
    }

    public void CheckExpiration()
    {
        if ((Status == MoneyRequestStatus.Pending || Status == MoneyRequestStatus.PartiallyPaid) &&
            ExpiresAtUtc.HasValue && ExpiresAtUtc.Value < DateTime.UtcNow)
        {
            Expire();
        }
    }
}
