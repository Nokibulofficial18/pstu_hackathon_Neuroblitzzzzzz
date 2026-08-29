using NCash.Application.Modules.RiskShield.DTOs;

namespace NCash.Application.Modules.PaymentEngine.DTOs;

public record InitiateTransferDto(
    string? ReceiverAccountNumber = null,
    decimal Amount = 0m,
    string? Purpose = null,
    bool ConfirmHighRisk = false,
    string? RecipientId = null)
{
    public string ResolvedRecipient =>
        !string.IsNullOrWhiteSpace(ReceiverAccountNumber)
            ? ReceiverAccountNumber.Trim()
            : (RecipientId?.Trim() ?? string.Empty);
}

public record ExecuteTransferCommand(
    Guid? SenderAccountId,
    Guid ReceiverAccountId,
    decimal Amount,
    string IdempotencyKey,
    NCash.Domain.Enums.TransactionType Type,
    string? Purpose = null,
    decimal Fee = 0m,
    bool BypassRiskCheck = false);

public record TransferResultDto(
    Guid TransactionId,
    string TransactionNumber,
    string? SenderAccountNumber,
    string? SenderUsername,
    string ReceiverAccountNumber,
    string ReceiverUsername,
    decimal Amount,
    decimal Fee,
    decimal? PreviousSenderBalance,
    decimal? SenderNewBalance,
    decimal ReceiverNewBalance,
    string Status,
    string IdempotencyKey,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    string RiskLevel,
    int RiskScore,
    bool ZeroVarianceVerified,
    decimal LedgerDelta,
    RiskAssessmentResultDto? RiskAssessment,
    List<string> EventTimeline,
    bool IsCached = false);

public record TransactionEventDto(
    string EventType,
    string Description,
    string? MetadataJson,
    DateTime TimestampUtc);

public record LedgerEntrySummaryDto(
    Guid Id,
    string AccountNumber,
    string Direction,
    decimal Amount,
    decimal BalanceAfter,
    string Description,
    DateTime CreatedAtUtc);

public record ReconciliationSummaryDto(
    bool IsZeroVariance,
    decimal TotalDebits,
    decimal TotalCredits,
    decimal NetVariance,
    string StatusDescription);

public record TransactionDetailDto(
    Guid Id,
    string TransactionNumber,
    string? SenderUsername,
    string? SenderAccountNumber,
    string ReceiverUsername,
    string ReceiverAccountNumber,
    decimal Amount,
    decimal Fee,
    string Status,
    string Type,
    string IdempotencyKey,
    string? Purpose,
    int RiskScore,
    string RiskLevel,
    string? FailureReason,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    List<TransactionEventDto> Timeline,
    List<RiskSignalDto> RiskSignals,
    List<LedgerEntrySummaryDto> LedgerEntries,
    ReconciliationSummaryDto Reconciliation);
