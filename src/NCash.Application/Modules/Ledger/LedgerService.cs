using Microsoft.EntityFrameworkCore;
using NCash.Application.Contracts.Persistence;
using NCash.Domain.Common;
using NCash.Domain.Enums;

namespace NCash.Application.Modules.Ledger;

public record LedgerEntryDetailDto(
    Guid Id,
    Guid TransactionId,
    string AccountNumber,
    string Direction,
    decimal Amount,
    decimal BalanceAfter,
    string Description,
    DateTime CreatedAtUtc);

public record GlobalReconciliationDto(
    decimal TotalDebits,
    decimal TotalCredits,
    decimal NetVariance,
    bool IsZeroVariance,
    decimal TotalTreasuryIssued,
    decimal TotalCirculatingUserBalance,
    bool IsSystemConservationMaintained,
    DateTime CheckedAtUtc);

public interface ILedgerService
{
    Task<List<LedgerEntryDetailDto>> GetAccountLedgerEntriesAsync(Guid userId, int limit = 50, CancellationToken cancellationToken = default);
    Task<GlobalReconciliationDto> GetGlobalReconciliationAsync(CancellationToken cancellationToken = default);
}

public class LedgerService : ILedgerService
{
    private readonly IApplicationDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly ILedgerRepository _ledgerRepository;

    public LedgerService(
        IApplicationDbContext context,
        IAccountRepository accountRepository,
        ILedgerRepository ledgerRepository)
    {
        _context = context;
        _accountRepository = accountRepository;
        _ledgerRepository = ledgerRepository;
    }

    public async Task<List<LedgerEntryDetailDto>> GetAccountLedgerEntriesAsync(Guid userId, int limit = 50, CancellationToken cancellationToken = default)
    {
        var account = await _accountRepository.GetByUserIdAsync(userId, cancellationToken);
        if (account == null)
            throw new DomainException(ErrorCodes.AccountNotFound, "Account not found.");

        var entries = await _ledgerRepository.GetEntriesByAccountIdAsync(account.Id, limit, cancellationToken);

        return entries.Select(e => new LedgerEntryDetailDto(
            e.Id,
            e.TransactionId,
            account.AccountNumber,
            e.Direction.ToString(),
            e.Amount,
            e.BalanceAfter,
            e.Description,
            e.CreatedAtUtc)).ToList();
    }

    public async Task<GlobalReconciliationDto> GetGlobalReconciliationAsync(CancellationToken cancellationToken = default)
    {
        var (totalDebits, totalCredits, netSum, isBalanced) = await _ledgerRepository.CheckGlobalReconciliationAsync(cancellationToken);

        // Calculate total circulating balance (all non-treasury accounts)
        var totalCirculatingUserBalance = await _context.Accounts
            .Where(a => a.Id != SystemConstants.TreasuryAccountId)
            .SumAsync(a => (decimal?)a.Balance, cancellationToken) ?? 0m;

        // Total issued by Treasury (all debits from treasury account)
        var totalTreasuryIssued = await _context.LedgerEntries
            .Where(l => l.AccountId == SystemConstants.TreasuryAccountId && l.Direction == LedgerDirection.Debit)
            .SumAsync(l => (decimal?)l.Amount, cancellationToken) ?? 0m;

        var isConservationMaintained = (totalTreasuryIssued == totalCirculatingUserBalance);

        return new GlobalReconciliationDto(
            totalDebits,
            totalCredits,
            netSum,
            isBalanced,
            totalTreasuryIssued,
            totalCirculatingUserBalance,
            isConservationMaintained,
            DateTime.UtcNow);
    }
}
