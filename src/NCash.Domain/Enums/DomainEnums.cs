namespace NCash.Domain.Enums;

public enum UserStatus
{
    Active = 1,
    Suspended = 2,
    Deactivated = 3
}

public enum TransactionStatus
{
    Created = 1,
    Processing = 2,
    Succeeded = 3,
    Failed = 4,
    Unknown = 5,
    Recovering = 6,
    Cancelled = 7
}

public static class TransactionEventTypes
{
    public const string Created = "CREATED";
    public const string Validated = "VALIDATED";
    public const string Processing = "PROCESSING";
    public const string Debited = "DEBITED";
    public const string Credited = "CREDITED";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Unknown = "UNKNOWN";
    public const string RecoveryStarted = "RECOVERY_STARTED";
    public const string Recovered = "RECOVERED";
}

public enum TransactionType
{
    Transfer = 1,
    SystemIssuance = 2,
    MoneyRequestPayment = 3,
    GroupCollectionPayment = 4,
    Hold = 5,
    Release = 6,
    Refund = 7
}

public enum LedgerDirection
{
    Debit = 1,  // Money leaving account (deduction)
    Credit = 2  // Money entering account (addition)
}

public enum AccountStatus
{
    Active = 1,
    Frozen = 2,
    Suspended = 3,
    Closed = 4
}

public enum MoneyRequestStatus
{
    Pending = 1,
    PartiallyPaid = 2,
    Paid = 3,
    Rejected = 4,
    Cancelled = 5,
    Expired = 6
}

public enum RiskLevel
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum DisputeStatus
{
    Open = 1,
    UnderReview = 2,
    Resolved = 3,
    Rejected = 4
}

public enum IdempotencyStatus
{
    Processing = 1,
    Completed = 2,
    Failed = 3
}

public enum GroupCollectionStatus
{
    Pending = 1,
    PartiallyPaid = 2,
    Paid = 3,
    Cancelled = 4,
    Expired = 5
}

public enum GroupMemberStatus
{
    Pending = 1,
    PartiallyPaid = 2,
    Paid = 3,
    Declined = 4
}

