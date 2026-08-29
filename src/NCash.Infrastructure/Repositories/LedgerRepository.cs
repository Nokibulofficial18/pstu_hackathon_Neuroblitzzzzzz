using Microsoft.EntityFrameworkCore;
using NCash.Application.Contracts.Persistence;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;

namespace NCash.Infrastructure.Repositories;

public class LedgerRepository : ILedgerRepository
{
    private readonly NCashDbContext _context;

    public LedgerRepository(NCashDbContext context)
    {
        _context = context;
    }

    public async Task AddEntryAsync(LedgerEntry entry, CancellationToken cancellationToken = default)
    {
        await _context.LedgerEntries.AddAsync(entry, cancellationToken);
    }

    public async Task<List<LedgerEntry>> GetEntriesByAccountIdAsync(Guid accountId, int limit = 50, CancellationToken cancellationToken = default)
    {
        return await _context.LedgerEntries
            .Include(l => l.Transaction)
            .Where(l => l.AccountId == accountId)
            .OrderByDescending(l => l.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<LedgerEntry>> GetEntriesByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        return await _context.LedgerEntries
            .Include(l => l.Account).ThenInclude(a => a.User)
            .Where(l => l.TransactionId == transactionId)
            .OrderBy(l => l.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<(decimal TotalDebits, decimal TotalCredits, decimal NetSum, bool IsBalanced)> CheckGlobalReconciliationAsync(CancellationToken cancellationToken = default)
    {
        var totalDebits = await _context.LedgerEntries
            .Where(l => l.Direction == LedgerDirection.Debit)
            .SumAsync(l => (decimal?)l.Amount, cancellationToken) ?? 0m;

        var totalCredits = await _context.LedgerEntries
            .Where(l => l.Direction == LedgerDirection.Credit)
            .SumAsync(l => (decimal?)l.Amount, cancellationToken) ?? 0m;

        var netSum = totalCredits - totalDebits;
        var isBalanced = (netSum == 0m);

        return (totalDebits, totalCredits, netSum, isBalanced);
    }
}
