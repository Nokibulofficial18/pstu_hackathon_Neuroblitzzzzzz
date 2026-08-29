using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Domain.Entities;

public class RiskSignal : BaseEntity
{
    public Guid TransactionId { get; private set; }
    public string RuleCode { get; private set; } = string.Empty;
    public int ScoreImpact { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public RiskLevel Severity { get; private set; } = RiskLevel.Low;

    // Navigation property
    public virtual Transaction Transaction { get; private set; } = null!;

    private RiskSignal() { } // EF Core

    public RiskSignal(Guid transactionId, string ruleCode, int scoreImpact, string reason, RiskLevel severity = RiskLevel.Low)
    {
        TransactionId = transactionId;
        RuleCode = ruleCode;
        ScoreImpact = scoreImpact;
        Reason = reason;
        Severity = severity;
    }
}
