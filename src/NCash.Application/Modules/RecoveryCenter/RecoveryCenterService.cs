using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.PaymentEngine;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;

namespace NCash.Application.Modules.RecoveryCenter;

public record CreateRecoveryCaseDto(
    Guid TransactionId,
    string IssueType,
    string Description);

public record RecoveryCaseDetailDto(
    Guid CaseId,
    Guid TransactionId,
    string TransactionNumber,
    decimal TransactionAmount,
    string CurrentTransactionState,
    Guid ReporterUserId,
    string ReporterUsername,
    string IssueType,
    string Description,
    string RecoveryStatus,
    string? Resolution,
    string AuditDiagnosis,
    DateTime CreatedAtUtc,
    DateTime? ResolvedAtUtc)
{
    public Guid Id => CaseId;
    public string Status => RecoveryStatus;
    public DateTime CreatedAt => CreatedAtUtc;
    public Guid TxnId => TransactionId;
}

public record ResolveRecoveryCaseDto(
    DisputeStatus Status,
    string Resolution);

public interface IRecoveryCenterService
{
    Task<RecoveryCaseDetailDto> FileRecoveryCaseAsync(Guid currentUserId, CreateRecoveryCaseDto dto, CancellationToken cancellationToken = default);
    Task<RecoveryCaseDetailDto> GetRecoveryCaseByIdAsync(Guid caseId, Guid currentUserId, CancellationToken cancellationToken = default);
    Task<List<RecoveryCaseDetailDto>> GetUserRecoveryCasesAsync(Guid currentUserId, CancellationToken cancellationToken = default);
    Task<List<RecoveryCaseDetailDto>> GetAllRecoveryCasesAsync(CancellationToken cancellationToken = default);
    Task<RecoveryCaseDetailDto> InvestigateAndResolveCaseAsync(Guid caseId, CancellationToken cancellationToken = default);
    Task<RecoveryCaseDetailDto> ManualResolveCaseAsync(Guid caseId, ResolveRecoveryCaseDto dto, CancellationToken cancellationToken = default);
}

public class RecoveryCenterService : IRecoveryCenterService
{
    private readonly IApplicationDbContext _context;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly IPaymentEngine _paymentEngine;
    private readonly ILogger<RecoveryCenterService> _logger;

    public RecoveryCenterService(
        IApplicationDbContext context,
        ITransactionRepository transactionRepository,
        ILedgerRepository ledgerRepository,
        IAccountRepository accountRepository,
        IPaymentEngine paymentEngine,
        ILogger<RecoveryCenterService> logger)
    {
        _context = context;
        _transactionRepository = transactionRepository;
        _ledgerRepository = ledgerRepository;
        _accountRepository = accountRepository;
        _paymentEngine = paymentEngine;
        _logger = logger;
    }

    public async Task<RecoveryCaseDetailDto> FileRecoveryCaseAsync(Guid currentUserId, CreateRecoveryCaseDto dto, CancellationToken cancellationToken = default)
    {
        var txn = await _transactionRepository.GetByIdAsync(dto.TransactionId, cancellationToken);
        if (txn == null)
            throw new DomainException(ErrorCodes.TransactionNotFound, "Transaction not found.", 404);

        var reporterAccount = await _accountRepository.GetByUserIdAsync(currentUserId, cancellationToken);
        if (reporterAccount == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "User account not found.");

        if (txn.SenderAccountId != reporterAccount.Id && txn.ReceiverAccountId != reporterAccount.Id)
        {
            throw new DomainException(ErrorCodes.UnauthorizedAccess, "You can only file recovery cases for transactions involving your account.", 403);
        }

        if (await _context.DisputeCases.AnyAsync(d => d.TransactionId == dto.TransactionId && d.ReportedByUserId == currentUserId && d.Status == DisputeStatus.Open, cancellationToken))
        {
            throw new DomainException(ErrorCodes.DisputeAlreadyExists, "An active recovery case is already open for this transaction.");
        }

        var recoveryCase = new DisputeCase(dto.TransactionId, currentUserId, dto.IssueType, dto.Description);
        await _context.DisputeCases.AddAsync(recoveryCase, cancellationToken);

        var audit = new TransactionEvent(txn.Id, "RECOVERY_REPORTED", $"Recovery case filed ({dto.IssueType}): {dto.Description}");
        await _context.TransactionEvents.AddAsync(audit, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Recovery case {CaseId} created for Transaction {TxnId} by User {UserId}",
            recoveryCase.Id, txn.Id, currentUserId);

        return await MapToDtoAsync(recoveryCase.Id, cancellationToken);
    }

    public async Task<RecoveryCaseDetailDto> GetRecoveryCaseByIdAsync(Guid caseId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var recoveryCase = await _context.DisputeCases
            .Include(d => d.Transaction)
            .Include(d => d.ReportedByUser)
            .FirstOrDefaultAsync(d => d.Id == caseId, cancellationToken);

        if (recoveryCase == null)
            throw new DomainException(ErrorCodes.TransactionAlreadyProcessed, "Recovery case not found.", 404);

        if (recoveryCase.ReportedByUserId != currentUserId)
        {
            // Allow admin/auditor or throw
            var user = await _context.Users.FindAsync([currentUserId], cancellationToken);
            if (user == null || (user.Role != "Admin" && user.Role != "Auditor"))
            {
                throw new DomainException(ErrorCodes.UnauthorizedAccess, "You are not authorized to view this recovery case.", 403);
            }
        }

        return await MapToDtoAsync(caseId, cancellationToken);
    }

    public async Task<List<RecoveryCaseDetailDto>> GetUserRecoveryCasesAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var cases = await _context.DisputeCases
            .Include(d => d.Transaction)
            .Include(d => d.ReportedByUser)
            .Where(d => d.ReportedByUserId == currentUserId)
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var dtos = new List<RecoveryCaseDetailDto>();
        foreach (var c in cases)
        {
            dtos.Add(await BuildDtoAsync(c, cancellationToken));
        }
        return dtos;
    }

    public async Task<List<RecoveryCaseDetailDto>> GetAllRecoveryCasesAsync(CancellationToken cancellationToken = default)
    {
        var cases = await _context.DisputeCases
            .Include(d => d.Transaction)
            .Include(d => d.ReportedByUser)
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var dtos = new List<RecoveryCaseDetailDto>();
        foreach (var c in cases)
        {
            dtos.Add(await BuildDtoAsync(c, cancellationToken));
        }
        return dtos;
    }

    public async Task<RecoveryCaseDetailDto> InvestigateAndResolveCaseAsync(Guid caseId, CancellationToken cancellationToken = default)
    {
        var recoveryCase = await _context.DisputeCases
            .Include(d => d.Transaction)
            .FirstOrDefaultAsync(d => d.Id == caseId, cancellationToken);

        if (recoveryCase == null)
            throw new DomainException(ErrorCodes.TransactionAlreadyProcessed, "Recovery case not found.", 404);

        var txn = await _transactionRepository.GetByIdAsync(recoveryCase.TransactionId, cancellationToken);
        if (txn == null)
            throw new DomainException(ErrorCodes.TransactionAlreadyProcessed, "Associated transaction not found.", 404);

        var ledgerEntries = await _ledgerRepository.GetEntriesByTransactionIdAsync(txn.Id, cancellationToken);
        var totalDebits = ledgerEntries.Where(l => l.Direction == LedgerDirection.Debit).Sum(l => l.Amount);
        var totalCredits = ledgerEntries.Where(l => l.Direction == LedgerDirection.Credit).Sum(l => l.Amount);
        var isBalanced = ledgerEntries.Count >= 2 && (totalDebits == totalCredits) && (totalDebits == txn.Amount);

        recoveryCase.MarkUnderReview();

        if (txn.Status == TransactionStatus.Unknown || txn.Status == TransactionStatus.Recovering)
        {
            txn.MarkRecovering();
            var recoveryStartedEvent = new TransactionEvent(txn.Id, TransactionEventTypes.RecoveryStarted, "Automatic recovery diagnosis initiated.");
            await _context.TransactionEvents.AddAsync(recoveryStartedEvent, cancellationToken);

            if (isBalanced)
            {
                txn.MarkSucceeded();
                var recoveredEvent = new TransactionEvent(txn.Id, TransactionEventTypes.Recovered, "Verified ledger double-entry zero variance. Transaction safely confirmed COMPLETED.");
                await _context.TransactionEvents.AddAsync(recoveredEvent, cancellationToken);

                recoveryCase.Resolve($"Audit confirmed complete atomic settlement. Debited BDT {totalDebits:N2}, Credited BDT {totalCredits:N2}. Zero variance verified.");
            }
            else if (ledgerEntries.Count == 0)
            {
                txn.MarkFailed("Transaction was aborted prior to commitment. No funds were debited.");
                var failedEvent = new TransactionEvent(txn.Id, TransactionEventTypes.Failed, "Audit confirmed atomic rollback. No balances were modified.");
                await _context.TransactionEvents.AddAsync(failedEvent, cancellationToken);

                recoveryCase.Resolve("Audit confirmed transaction safely aborted prior to balance deduction. Sender balance is intact.");
            }
            _transactionRepository.Update(txn);
        }
        else if (txn.Status == TransactionStatus.Succeeded)
        {
            recoveryCase.Resolve($"Transaction is verified SUCCEEDED with {ledgerEntries.Count} immutable ledger entries. Recipient account possesses the funds.");
        }
        else if (txn.Status == TransactionStatus.Failed)
        {
            recoveryCase.Resolve("Transaction is confirmed FAILED. Zero funds were deducted from sender.");
        }

        _context.DisputeCases.Update(recoveryCase);
        await _context.SaveChangesAsync(cancellationToken);

        return await MapToDtoAsync(caseId, cancellationToken);
    }

    public async Task<RecoveryCaseDetailDto> ManualResolveCaseAsync(Guid caseId, ResolveRecoveryCaseDto dto, CancellationToken cancellationToken = default)
    {
        var recoveryCase = await _context.DisputeCases
            .Include(d => d.Transaction)
            .FirstOrDefaultAsync(d => d.Id == caseId, cancellationToken);

        if (recoveryCase == null)
            throw new DomainException(ErrorCodes.TransactionAlreadyProcessed, "Recovery case not found.", 404);

        if (dto.Status == DisputeStatus.Resolved)
        {
            recoveryCase.Resolve(dto.Resolution);
        }
        else if (dto.Status == DisputeStatus.Rejected)
        {
            recoveryCase.Reject(dto.Resolution);
        }

        _context.DisputeCases.Update(recoveryCase);
        await _context.SaveChangesAsync(cancellationToken);

        return await MapToDtoAsync(caseId, cancellationToken);
    }

    private async Task<RecoveryCaseDetailDto> MapToDtoAsync(Guid caseId, CancellationToken ct)
    {
        var c = await _context.DisputeCases
            .Include(x => x.Transaction)
            .Include(x => x.ReportedByUser)
            .FirstAsync(x => x.Id == caseId, ct);

        return await BuildDtoAsync(c, ct);
    }

    private async Task<RecoveryCaseDetailDto> BuildDtoAsync(DisputeCase c, CancellationToken ct)
    {
        var ledgerEntries = await _ledgerRepository.GetEntriesByTransactionIdAsync(c.TransactionId, ct);
        var totalDebits = ledgerEntries.Where(l => l.Direction == LedgerDirection.Debit).Sum(l => l.Amount);
        var totalCredits = ledgerEntries.Where(l => l.Direction == LedgerDirection.Credit).Sum(l => l.Amount);
        var variance = totalDebits - totalCredits;

        string auditDiagnosis = $"State: {c.Transaction?.Status.ToString() ?? "Unknown"}. Ledger Entries: {ledgerEntries.Count} (Debits: {totalDebits:N2} BDT, Credits: {totalCredits:N2} BDT, Variance: {variance:N2} BDT).";

        return new RecoveryCaseDetailDto(
            c.Id,
            c.TransactionId,
            c.Transaction?.TransactionNumber ?? "TXN-UNKNOWN",
            c.Transaction?.Amount ?? 0m,
            c.Transaction?.Status.ToString() ?? "Unknown",
            c.ReportedByUserId,
            c.ReportedByUser?.Username ?? "User",
            c.Category,
            c.Description,
            c.Status.ToString(),
            c.ResolutionNote,
            auditDiagnosis,
            c.CreatedAtUtc,
            c.ResolvedAtUtc);
    }
}
