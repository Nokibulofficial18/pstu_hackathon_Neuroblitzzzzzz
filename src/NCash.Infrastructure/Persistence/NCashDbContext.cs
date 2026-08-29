using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NCash.Application.Contracts.Persistence;
using NCash.Domain.Entities;

namespace NCash.Infrastructure.Persistence;

public class NCashDbContext : DbContext, IApplicationDbContext
{
    public NCashDbContext(DbContextOptions<NCashDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<TransactionEvent> TransactionEvents => Set<TransactionEvent>();
    public DbSet<MoneyRequest> MoneyRequests => Set<MoneyRequest>();
    public DbSet<RiskSignal> RiskSignals => Set<RiskSignal>();
    public DbSet<DisputeCase> DisputeCases => Set<DisputeCase>();
    public DbSet<SystemAuditLog> SystemAuditLogs => Set<SystemAuditLog>();
    public DbSet<GroupCollection> GroupCollections => Set<GroupCollection>();
    public DbSet<GroupCollectionMember> GroupCollectionMembers => Set<GroupCollectionMember>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NCashDbContext).Assembly);
    }

    public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        return await Database.BeginTransactionAsync(cancellationToken);
    }
}
