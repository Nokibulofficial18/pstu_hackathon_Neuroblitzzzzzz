using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Domain.Entities;

public class Account : BaseEntity
{
    public Guid UserId { get; private set; }
    public string AccountNumber { get; private set; } = string.Empty;
    public decimal Balance { get; private set; }
    public string Currency { get; private set; } = "BDT";
    public AccountStatus Status { get; private set; } = AccountStatus.Active;
    public uint Version { get; private set; } = 1;

    // Navigation properties
    public virtual User User { get; private set; } = null!;
    public virtual ICollection<LedgerEntry> LedgerEntries { get; private set; } = new List<LedgerEntry>();

    private Account() { } // EF Core

    public Account(Guid userId, string accountNumber, decimal initialBalance = 0m, string currency = "BDT")
    {
        if (initialBalance < 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Initial balance cannot be negative.");

        UserId = userId;
        AccountNumber = accountNumber;
        Balance = initialBalance;
        Currency = currency.ToUpperInvariant();
        Status = AccountStatus.Active;
        Version = 1;
    }

    public bool CanDebit(decimal amount)
    {
        return Status == AccountStatus.Active && amount > 0 && Balance >= amount;
    }

    public void Debit(decimal amount)
    {
        if (Status != AccountStatus.Active)
            throw new DomainException(ErrorCodes.AccountInactive, $"Account {AccountNumber} is {Status} and cannot be debited.");

        if (amount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Debit amount must be strictly greater than zero.");

        if (Balance < amount)
            throw new DomainException(ErrorCodes.InsufficientFunds, $"Insufficient balance. Available: {Balance} {Currency}, Requested: {amount} {Currency}.");

        Balance -= amount;
        Version++;
        Touch();
    }

    public void Credit(decimal amount)
    {
        if (Status != AccountStatus.Active)
            throw new DomainException(ErrorCodes.AccountInactive, $"Account {AccountNumber} is {Status} and cannot receive credits.");

        if (amount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Credit amount must be strictly greater than zero.");

        Balance += amount;
        Version++;
        Touch();
    }

    public void Freeze()
    {
        Status = AccountStatus.Frozen;
        Touch();
    }

    public void Unfreeze()
    {
        Status = AccountStatus.Active;
        Touch();
    }

    public void Suspend()
    {
        Status = AccountStatus.Suspended;
        Touch();
    }
}
