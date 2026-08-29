using NCash.Domain.Entities;

namespace NCash.Application.Contracts.Persistence;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Account?> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Account?> GetByAccountNumberAsync(string accountNumber, CancellationToken cancellationToken = default);
    Task<Account?> GetByIdentifierAsync(string identifier, CancellationToken cancellationToken = default);
    Task<(Account? Sender, Account? Receiver)> GetAccountsForUpdateAsync(Guid senderId, Guid receiverId, CancellationToken cancellationToken = default);
    Task<Account?> GetAccountForUpdateAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task AddAsync(Account account, CancellationToken cancellationToken = default);
    void Update(Account account);
}

public interface ITransactionRepository
{
    Task<Transaction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Transaction?> GetByNumberAsync(string transactionNumber, CancellationToken cancellationToken = default);
    Task<Transaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default);
    Task<List<Transaction>> GetAccountHistoryAsync(Guid accountId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);
    Task<int> GetRecentTransactionCountAsync(Guid senderAccountId, TimeSpan window, CancellationToken cancellationToken = default);
    Task<bool> HasTransactedWithAsync(Guid senderAccountId, Guid receiverAccountId, CancellationToken cancellationToken = default);
    Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default);
    void Update(Transaction transaction);
}

public interface ILedgerRepository
{
    Task AddEntryAsync(LedgerEntry entry, CancellationToken cancellationToken = default);
    Task<List<LedgerEntry>> GetEntriesByAccountIdAsync(Guid accountId, int limit = 50, CancellationToken cancellationToken = default);
    Task<List<LedgerEntry>> GetEntriesByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken = default);
    Task<(decimal TotalDebits, decimal TotalCredits, decimal NetSum, bool IsBalanced)> CheckGlobalReconciliationAsync(CancellationToken cancellationToken = default);
}

public interface IIdempotencyRepository
{
    Task<IdempotencyRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);
    Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken = default);
    Task UpdateAsync(IdempotencyRecord record, CancellationToken cancellationToken = default);
}
