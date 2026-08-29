using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Domain.Entities;

public class User : BaseEntity
{
    public string FullName { get; private set; } = string.Empty;
    public string Username { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string PhoneNumber { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string? TransactionPinHash { get; private set; }
    public UserStatus Status { get; private set; } = UserStatus.Active;
    public string Role { get; private set; } = "User"; // User, Admin, Auditor
    public bool IsActive => Status == UserStatus.Active;

    // Navigation property (1 User : 1 Account)
    public virtual Account? Account { get; private set; }

    private User() { } // EF Core

    public User(
        string fullName,
        string username,
        string email,
        string phoneNumber,
        string passwordHash,
        string? transactionPinHash = null,
        string role = "User",
        UserStatus status = UserStatus.Active)
    {
        FullName = fullName.Trim();
        Username = username.Trim().ToLowerInvariant();
        Email = email.Trim().ToLowerInvariant();
        PhoneNumber = phoneNumber.Trim();
        PasswordHash = passwordHash;
        TransactionPinHash = transactionPinHash;
        Role = role;
        Status = status;
    }

    public void SetTransactionPin(string pinHash)
    {
        TransactionPinHash = pinHash;
        Touch();
    }

    public void UpdateProfile(string fullName, string phoneNumber)
    {
        FullName = fullName.Trim();
        PhoneNumber = phoneNumber.Trim();
        Touch();
    }

    public void SetAccount(Account account)
    {
        Account = account;
    }

    public void Deactivate()
    {
        Status = UserStatus.Deactivated;
        Touch();
    }

    public void Suspend()
    {
        Status = UserStatus.Suspended;
        Touch();
    }

    public void Activate()
    {
        Status = UserStatus.Active;
        Touch();
    }
}
