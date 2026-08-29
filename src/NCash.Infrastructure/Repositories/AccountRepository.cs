using Microsoft.EntityFrameworkCore;
using NCash.Application.Contracts.Persistence;
using NCash.Domain.Entities;
using NCash.Infrastructure.Persistence;

namespace NCash.Infrastructure.Repositories;

public class AccountRepository : IAccountRepository
{
    private readonly NCashDbContext _context;

    public AccountRepository(NCashDbContext context)
    {
        _context = context;
    }

    public async Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task<Account?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _context.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
    }

    public async Task<Account?> GetByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken = default)
    {
        return await _context.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.AccountNumber == accountNumber, cancellationToken);
    }

    public async Task<Account?> GetAccountForUpdateAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            var sql = "SELECT * FROM accounts WHERE \"Id\" = {0} FOR UPDATE";
            return await _context.Accounts
                .FromSqlRaw(sql, accountId)
                .Include(a => a.User)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await _context.Accounts
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
    }

    public async Task<(Account? Sender, Account? Receiver)> GetAccountsForUpdateAsync(
        Guid senderId,
        Guid receiverId,
        CancellationToken cancellationToken = default)
    {
        var orderedIds = senderId.CompareTo(receiverId) < 0
            ? new[] { senderId, receiverId }
            : new[] { receiverId, senderId };

        List<Account> accounts;

        if (_context.Database.IsNpgsql())
        {
            var sql = "SELECT * FROM accounts WHERE \"Id\" = ANY({0}) ORDER BY \"Id\" FOR UPDATE";
            accounts = await _context.Accounts
                .FromSqlRaw(sql, (object)orderedIds)
                .Include(a => a.User)
                .ToListAsync(cancellationToken);
        }
        else
        {
            accounts = await _context.Accounts
                .Include(a => a.User)
                .Where(a => orderedIds.Contains(a.Id))
                .ToListAsync(cancellationToken);
        }

        var sender = accounts.FirstOrDefault(a => a.Id == senderId);
        var receiver = accounts.FirstOrDefault(a => a.Id == receiverId);

        return (sender, receiver);
    }

    public async Task AddAsync(Account account, CancellationToken cancellationToken = default)
    {
        await _context.Accounts.AddAsync(account, cancellationToken);
    }

    public void Update(Account account)
    {
        var entry = _context.Entry(account);
        if (entry.State == EntityState.Detached)
        {
            _context.Accounts.Update(account);
        }
    }
}
