namespace NCash.Application.Modules.TrustLab.DTOs;

public record DuplicateTestResultDto(
    string TestName,
    string IdempotencyKey,
    int RequestedAttempts,
    int SuccessfulFinancialEffects,
    int DuplicateAttemptsBlocked,
    decimal InitialBalance,
    decimal FinalBalance,
    decimal TotalDeducted,
    Guid TransactionId,
    string TransactionNumber,
    bool Passed,
    string Summary);

public record ConcurrencyTestResultDto(
    string TestName,
    decimal InitialBalance,
    decimal TransferAmountA,
    decimal TransferAmountB,
    int SucceededCount,
    int FailedDueToInsufficientFundsCount,
    decimal FinalBalance,
    bool OverdraftOccurred,
    bool Passed,
    string Summary);

public record NetworkRetryTestResultDto(
    string TestName,
    string IdempotencyKey,
    Guid TransactionId,
    string TransactionNumber,
    string InitialAttemptStatus,
    string RetryAttemptStatus,
    decimal Amount,
    decimal TotalDeducted,
    int DeductionsCount,
    bool Passed,
    string Summary);

public record TimeoutRecoveryTestResultDto(
    string TestName,
    Guid TransactionId,
    string TransactionNumber,
    string InitialState,
    string SimulatedState,
    string RecoveringState,
    string FinalResolvedState,
    int LedgerEntriesCount,
    bool ZeroVarianceConfirmed,
    bool Passed,
    string Summary);

public record InvalidInputTestCaseDto(
    string CaseName,
    string Description,
    string ErrorCode,
    string ErrorMessage,
    bool RejectedSafely);

public record InvalidInputTestResultDto(
    string TestName,
    int TotalAttempts,
    int TotalSafelyRejected,
    int FinancialMutationsCount,
    List<InvalidInputTestCaseDto> TestCases,
    bool Passed,
    string Summary);

public record AccountLedgerCheckDto(
    string AccountNumber,
    string Username,
    decimal StoredBalance,
    decimal CalculatedLedgerBalance,
    decimal Difference,
    bool IsConsistent);

public record LedgerIntegrityReportDto(
    string TestName,
    decimal TotalDebits,
    decimal TotalCredits,
    decimal Difference,
    string HealthStatus,
    bool IsZeroVariance,
    int TotalAccountsChecked,
    int InconsistentAccountsCount,
    List<AccountLedgerCheckDto> AccountAudits,
    bool Passed,
    string Summary);

public record RepeatedRequestTestResultDto(
    string TestName,
    Guid RequestId,
    decimal RequestedAmount,
    decimal Payment1Amount,
    string Payment1Status,
    decimal Payment2Amount,
    string Payment2Status,
    decimal Payment3AttemptAmount,
    string Payment3Status,
    decimal TotalPaid,
    string FinalRequestStatus,
    bool Passed,
    string Summary);
