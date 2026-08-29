using Microsoft.EntityFrameworkCore;
using NCash.Application.Contracts.Persistence;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;

namespace NCash.Infrastructure.Repositories;

public class TransactionRepository : ITransactionRepository
{
    private readonly NCashDbContext _context;

    public TransactionRepository(NCashDbContext context)
    {
        _context = context;
    }

    public async Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Transactions
            .Include(t => t.SenderAccount).ThenInclude(a => a!.User)
            .Include(t => t.ReceiverAccount).ThenInclude(a => a.User)
            .Include(t => t.Events.OrderBy(e => e.CreatedAtUtc))
            .Include(t => t.RiskSignals)
            .Include(t => t.LedgerEntries)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    public async Task<Transaction?> GetByNumberAsync(string transactionNumber, CancellationToken cancellationToken = default)
    {
        return await _context.Transactions
            .Include(t => t.SenderAccount).ThenInclude(a => a!.User)
            .Include(t => t.ReceiverAccount).ThenInclude(a => a.User)
            .Include(t => t.Events.OrderBy(e => e.CreatedAtUtc))
            .Include(t => t.RiskSignals)
            .Include(t => t.LedgerEntries)
            .FirstOrDefaultAsync(t => t.TransactionNumber == transactionNumber, cancellationToken);
    }

    public async Task<Transaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        return await _context.Transactions
            .Include(t => t.SenderAccount).ThenInclude(a => a!.User)
            .Include(t => t.ReceiverAccount).ThenInclude(a => a.User)
            .Include(t => t.Events.OrderBy(e => e.CreatedAtUtc))
            .Include(t => t.RiskSignals)
            .Include(t => t.LedgerEntries)
            .FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public async Task<List<Transaction>> GetAccountHistoryAsync(Guid accountId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        return await _context.Transactions
            .Include(t => t.SenderAccount).ThenInclude(a => a!.User)
            .Include(t => t.ReceiverAccount).ThenInclude(a => a.User)
            .Include(t => t.RiskSignals)
            .Where(t => t.SenderAccountId == accountId || t.ReceiverAccountId == accountId)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Transaction>> GetAllTransactionsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        return await _context.Transactions
            .Include(t => t.SenderAccount).ThenInclude(a => a!.User)
            .Include(t => t.ReceiverAccount).ThenInclude(a => a.User)
            .Include(t => t.RiskSignals)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetRecentTransactionCountAsync(Guid senderAccountId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.Subtract(window);
        return await _context.Transactions
            .CountAsync(t => t.SenderAccountId == senderAccountId && t.CreatedAtUtc >= cutoff, cancellationToken);
    }

    public async Task<bool> HasTransactedWithAsync(Guid senderAccountId, Guid receiverAccountId, CancellationToken cancellationToken = default)
    {
        return await _context.Transactions
            .AnyAsync(t => t.SenderAccountId == senderAccountId &&
                           t.ReceiverAccountId == receiverAccountId &&
                           t.Status == TransactionStatus.Succeeded, cancellationToken);
    }

    public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        await _context.Transactions.AddAsync(transaction, cancellationToken);
    }

    public void Update(Transaction transaction)
    {
        var entry = _context.Entry(transaction);
        if (entry.State == EntityState.Detached)
        {
            _context.Transactions.Update(transaction);
        }
    }
}
