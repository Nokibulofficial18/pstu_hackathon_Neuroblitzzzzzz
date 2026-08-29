namespace NCash.Domain.Common;

public static class ErrorCodes
{
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string UserAlreadyExists = "USER_ALREADY_EXISTS";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string UnauthorizedAccess = "UNAUTHORIZED_ACCESS";
    public const string ValidationFailed = "VALIDATION_FAILED";

    public const string AccountNotFound = "ACCOUNT_NOT_FOUND";
    public const string AccountInactive = "ACCOUNT_INACTIVE";
    public const string AccountFrozen = "ACCOUNT_FROZEN";

    public const string InsufficientFunds = "INSUFFICIENT_FUNDS";
    public const string InvalidAmount = "INVALID_AMOUNT";
    public const string SelfTransferNotAllowed = "SELF_TRANSFER_NOT_ALLOWED";
    public const string RecipientNotFound = "RECIPIENT_NOT_FOUND";

    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string TransactionNotFound = "TRANSACTION_NOT_FOUND";
    public const string TransactionAlreadyProcessed = "TRANSACTION_ALREADY_PROCESSED";
    public const string TransactionFailed = "TRANSACTION_FAILED";
    public const string ConcurrencyLockTimeout = "CONCURRENCY_LOCK_TIMEOUT";
    public const string InvalidTransactionState = "INVALID_TRANSACTION_STATE";

    public const string MoneyRequestNotFound = "MONEY_REQUEST_NOT_FOUND";
    public const string MoneyRequestAlreadyClosed = "MONEY_REQUEST_ALREADY_CLOSED";
    public const string MoneyRequestExpired = "MONEY_REQUEST_EXPIRED";
    public const string MoneyRequestInvalidAmount = "MONEY_REQUEST_INVALID_AMOUNT";

    public const string RiskAssessmentHigh = "RISK_ASSESSMENT_HIGH";
    public const string DisputeAlreadyExists = "DISPUTE_ALREADY_EXISTS";
    public const string DuplicateRequest = "DUPLICATE_REQUEST";
}
