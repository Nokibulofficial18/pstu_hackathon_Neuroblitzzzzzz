using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.RiskShield.DTOs;
using NCash.Domain.Enums;

namespace NCash.Application.Modules.RiskShield;

public interface IRiskShieldService
{
    Task<RiskAssessmentResultDto> AssessTransferRiskAsync(
        Guid senderAccountId,
        Guid receiverAccountId,
        decimal amount,
        CancellationToken cancellationToken = default);
}

public class RiskShieldService : IRiskShieldService
{
    private readonly IApplicationDbContext _context;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ILogger<RiskShieldService> _logger;

    public RiskShieldService(
        IApplicationDbContext context,
        ITransactionRepository transactionRepository,
        ILogger<RiskShieldService> logger)
    {
        _context = context;
        _transactionRepository = transactionRepository;
        _logger = logger;
    }

    public async Task<RiskAssessmentResultDto> AssessTransferRiskAsync(
        Guid senderAccountId,
        Guid receiverAccountId,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        var signals = new List<RiskSignalDto>();
        int totalScore = 0;

        // Rule 1: New Recipient Check (+30 pts)
        var hasHistory = await _transactionRepository.HasTransactedWithAsync(senderAccountId, receiverAccountId, cancellationToken);
        if (!hasHistory)
        {
            signals.Add(new RiskSignalDto(
                "NEW_RECIPIENT",
                30,
                "First-time transfer to this recipient account.",
                RiskLevel.Medium));
            totalScore += 30;
        }

        // Rule 2: Unusually Large Amount (+25 pts)
        if (amount >= 25000m)
        {
            signals.Add(new RiskSignalDto(
                "LARGE_AMOUNT",
                25,
                $"Transfer amount ({amount:N2} BDT) exceeds the standard safety monitoring threshold of 25,000.00 BDT.",
                RiskLevel.Medium));
            totalScore += 25;
        }

        // Rule 3: Many Transfers in Short Time / Burst Velocity (+20 pts)
        var burstCount = await _transactionRepository.GetRecentTransactionCountAsync(senderAccountId, TimeSpan.FromMinutes(2), cancellationToken);
        if (burstCount >= 3)
        {
            signals.Add(new RiskSignalDto(
                "BURST_VELOCITY",
                20,
                $"Rapid transfer frequency detected: {burstCount} transfers executed in the last 2 minutes.",
                RiskLevel.Medium));
            totalScore += 20;
        }

        // Rule 4: Repeated Failed PIN / Auth Attempts (+15 pts)
        var senderAccount = await _context.Accounts.FindAsync([senderAccountId], cancellationToken);
        if (senderAccount != null)
        {
            var fifteenMinutesAgo = DateTime.UtcNow.AddMinutes(-15);
            var failedAuthAttempts = await _context.SystemAuditLogs
                .CountAsync(l => l.ActorId == senderAccount.UserId &&
                                 (l.Action == "FAILED_LOGIN" || l.Action == "FAILED_PIN") &&
                                 l.CreatedAtUtc >= fifteenMinutesAgo, cancellationToken);

            if (failedAuthAttempts >= 1)
            {
                signals.Add(new RiskSignalDto(
                    "FAILED_PIN_ATTEMPTS",
                    15,
                    $"Recent failed authentication or PIN attempts ({failedAuthAttempts}) detected on account.",
                    RiskLevel.Medium));
                totalScore += 15;
            }
        }

        // Rule 5: Unusual Daily Transfer Frequency (+10 pts)
        var dailyCount = await _transactionRepository.GetRecentTransactionCountAsync(senderAccountId, TimeSpan.FromHours(24), cancellationToken);
        if (dailyCount >= 5)
        {
            signals.Add(new RiskSignalDto(
                "HIGH_DAILY_FREQUENCY",
                10,
                $"High 24-hour transaction frequency: {dailyCount} transfers in the last 24 hours.",
                RiskLevel.Low));
            totalScore += 10;
        }

        // Risk Bands:
        // 0 - 30: LOW (Normal confirmation)
        // 31 - 60: MEDIUM (Warning + additional confirmation)
        // 61 - 100: HIGH (Strong confirmation + PIN verification required)
        RiskLevel level;
        bool requiresConfirmation;
        bool requiresPin;
        string explanation;

        if (totalScore >= 61)
        {
            level = RiskLevel.High;
            requiresConfirmation = true;
            requiresPin = true;
            explanation = $"HIGH RISK ({totalScore}/100): Multiple high-impact risk signals detected. Requires explicit confirmation and transaction PIN verification.";
        }
        else if (totalScore >= 31)
        {
            level = RiskLevel.Medium;
            requiresConfirmation = true;
            requiresPin = false;
            explanation = $"MEDIUM RISK ({totalScore}/100): Elevated risk signal detected. Requires user confirmation.";
        }
        else
        {
            level = RiskLevel.Low;
            requiresConfirmation = false;
            requiresPin = false;
            explanation = $"LOW RISK ({totalScore}/100): Transaction is within standard safety thresholds.";
        }

        _logger.LogInformation("Deterministic Risk Assessment: Sender {SenderId} -> Receiver {ReceiverId}, Amount: {Amount}. Total Score: {Score}, Level: {Level}",
            senderAccountId, receiverAccountId, amount, totalScore, level);

        return new RiskAssessmentResultDto(totalScore, level, requiresConfirmation, requiresPin, explanation, signals);
    }
}
