using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using NCash.Domain.Entities;

namespace NCash.Application.Contracts.Persistence;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Account> Accounts { get; }
    DbSet<Transaction> Transactions { get; }
    DbSet<LedgerEntry> LedgerEntries { get; }
    DbSet<TransactionEvent> TransactionEvents { get; }
    DbSet<MoneyRequest> MoneyRequests { get; }
    DbSet<RiskSignal> RiskSignals { get; }
    DbSet<DisputeCase> DisputeCases { get; }
    DbSet<SystemAuditLog> SystemAuditLogs { get; }
    DbSet<GroupCollection> GroupCollections { get; }
    DbSet<GroupCollectionMember> GroupCollectionMembers { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}
