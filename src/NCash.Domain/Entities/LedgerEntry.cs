using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Domain.Entities;

public class LedgerEntry : BaseEntity
{
    public Guid TransactionId { get; private set; }
    public Guid AccountId { get; private set; }
    public LedgerDirection Direction { get; private set; }
    public decimal Amount { get; private set; }
    public decimal BalanceAfter { get; private set; }
    public string Description { get; private set; } = string.Empty;

    // Navigation properties
    public virtual Transaction Transaction { get; private set; } = null!;
    public virtual Account Account { get; private set; } = null!;

    private LedgerEntry() { } // EF Core

    public LedgerEntry(
        Guid transactionId,
        Guid accountId,
        LedgerDirection direction,
        decimal amount,
        decimal balanceAfter,
        string description)
    {
        if (amount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Ledger entry amount must be strictly greater than zero.");

        TransactionId = transactionId;
        AccountId = accountId;
        Direction = direction;
        Amount = amount;
        BalanceAfter = balanceAfter;
        Description = description;
    }
}
