using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Domain.Entities;

public class Transaction : BaseEntity
{
    public string TransactionNumber { get; private set; } = string.Empty;
    public Guid? SenderAccountId { get; private set; }
    public Guid ReceiverAccountId { get; private set; }
    public decimal Amount { get; private set; }
    public decimal Fee { get; private set; } = 0m;
    public TransactionStatus Status { get; private set; } = TransactionStatus.Created;
    public TransactionType Type { get; private set; } = TransactionType.Transfer;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string? Purpose { get; private set; }
    public int RiskScore { get; private set; } = 0;
    public RiskLevel RiskLevel { get; private set; } = RiskLevel.Low;
    public string? FailureReason { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    // Navigation properties
    public virtual Account? SenderAccount { get; private set; }
    public virtual Account ReceiverAccount { get; private set; } = null!;
    public virtual ICollection<LedgerEntry> LedgerEntries { get; private set; } = new List<LedgerEntry>();
    public virtual ICollection<TransactionEvent> Events { get; private set; } = new List<TransactionEvent>();
    public virtual ICollection<RiskSignal> RiskSignals { get; private set; } = new List<RiskSignal>();

    private Transaction() { } // EF Core

    public Transaction(
        string transactionNumber,
        Guid? senderAccountId,
        Guid receiverAccountId,
        decimal amount,
        string idempotencyKey,
        TransactionType type = TransactionType.Transfer,
        string? purpose = null,
        decimal fee = 0m)
    {
        if (amount <= 0)
            throw new DomainException(ErrorCodes.InvalidAmount, "Transaction amount must be positive.");

        if (senderAccountId.HasValue && senderAccountId.Value == receiverAccountId)
            throw new DomainException(ErrorCodes.SelfTransferNotAllowed, "Cannot transfer money to the same account.");

        TransactionNumber = transactionNumber;
        SenderAccountId = senderAccountId;
        ReceiverAccountId = receiverAccountId;
        Amount = amount;
        Fee = fee;
        IdempotencyKey = idempotencyKey;
        Type = type;
        Purpose = purpose;
        Status = TransactionStatus.Created;
    }

    public void MarkProcessing()
    {
        if (Status != TransactionStatus.Created && Status != TransactionStatus.Recovering)
            throw new DomainException(ErrorCodes.InvalidTransactionState, $"Cannot transition to Processing from {Status}.");

        Status = TransactionStatus.Processing;
        Touch();
    }

    public void MarkSucceeded()
    {
        Status = TransactionStatus.Succeeded;
        CompletedAtUtc = DateTime.UtcNow;
        Touch();
    }

    public void MarkFailed(string reason)
    {
        Status = TransactionStatus.Failed;
        FailureReason = reason;
        CompletedAtUtc = DateTime.UtcNow;
        Touch();
    }

    public void MarkRecovering()
    {
        Status = TransactionStatus.Recovering;
        Touch();
    }

    public void MarkUnknown(string reason)
    {
        Status = TransactionStatus.Unknown;
        FailureReason = reason;
        Touch();
    }

    public void SetRiskAssessment(int score, RiskLevel level)
    {
        RiskScore = score;
        RiskLevel = level;
        Touch();
    }
}
