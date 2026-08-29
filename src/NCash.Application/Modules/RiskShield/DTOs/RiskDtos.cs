using NCash.Domain.Enums;

namespace NCash.Application.Modules.RiskShield.DTOs;

public record RiskSignalDto(
    string RuleCode,
    int ScoreImpact,
    string Reason,
    RiskLevel Severity);

public record RiskAssessmentResultDto(
    int TotalScore,
    RiskLevel Level,
    bool RequiresStepUpConfirmation,
    bool RequiresPinVerification,
    string Explanation,
    List<RiskSignalDto> Signals);
