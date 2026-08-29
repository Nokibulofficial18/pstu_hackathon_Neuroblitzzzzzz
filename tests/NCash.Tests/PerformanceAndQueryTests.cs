using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NCash.Application.Contracts.Persistence;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using Xunit;

namespace NCash.Tests;

public class PerformanceAndQueryTests
{
    private readonly NCashDbContext _context;
    private readonly ITransactionRepository _transactionRepository;
    private readonly IAccountRepository _accountRepository;

    public PerformanceAndQueryTests()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_PerfQueries_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new NCashDbContext(options);
        _transactionRepository = new TransactionRepository(_context);
        _accountRepository = new AccountRepository(_context);
    }

    [Fact]
    public async Task Test_1_Paginated_Transaction_History_Limits_Query_Size_And_Preserves_Order()
    {
        var u = new User("Alice Perf", "alice.perf", "alice@perf.local", "+8801700000099", "hash");
        var a = new Account(u.Id, "ACC-PERF-1", 100000m, "BDT");
        u.SetAccount(a);

        var r = new User("Bob Counterparty", "bob.perf", "bob@perf.local", "+8801700000098", "hash");
        var ra = new Account(r.Id, "ACC-PERF-2", 10000m, "BDT");
        r.SetAccount(ra);

        await _context.Users.AddRangeAsync(u, r);
        await _context.Accounts.AddRangeAsync(a, ra);

        // Seed 100 chronological transactions
        var txns = new List<Transaction>();
        for (int i = 0; i < 100; i++)
        {
            var txn = new Transaction(
                $"TXN-PERF-{i:D4}",
                a.Id,
                ra.Id,
                100m + i,
                $"IDEMP-PERF-{i}",
                TransactionType.Transfer,
                $"Test #{i}");

            txn.MarkProcessing();
            txn.MarkSucceeded();
            txns.Add(txn);
        }

        await _context.Transactions.AddRangeAsync(txns);
        await _context.SaveChangesAsync();

        // Request Page 1 with pageSize = 15
        var page1 = await _transactionRepository.GetAccountHistoryAsync(a.Id, page: 1, pageSize: 15);
        page1.Should().HaveCount(15);

        // Request Page 2 with pageSize = 15
        var page2 = await _transactionRepository.GetAccountHistoryAsync(a.Id, page: 2, pageSize: 15);
        page2.Should().HaveCount(15);

        // Disjoint verification
        page1.Select(t => t.Id).Intersect(page2.Select(t => t.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Test_2_Indexed_Transaction_Lookup_By_TransactionNumber_And_Id()
    {
        var u = new User("Lookup User", "user.lookup", "lookup@test.local", "+8801700000088", "hash");
        var a = new Account(u.Id, "ACC-LOOKUP-1", 50000m, "BDT");
        u.SetAccount(a);

        var r = new User("Lookup Rec", "rec.lookup", "lookup.rec@test.local", "+8801700000087", "hash");
        var ra = new Account(r.Id, "ACC-LOOKUP-2", 5000m, "BDT");
        r.SetAccount(ra);

        await _context.Users.AddRangeAsync(u, r);
        await _context.Accounts.AddRangeAsync(a, ra);

        var targetTxnNumber = "TXN-INDEXED-TARGET-007";
        var txn = new Transaction(targetTxnNumber, a.Id, ra.Id, 2500m, "IDEMP-LOOKUP-007", TransactionType.Transfer, "Target lookup");
        await _context.Transactions.AddAsync(txn);
        await _context.SaveChangesAsync();

        var foundByNumber = await _transactionRepository.GetByNumberAsync(targetTxnNumber);
        foundByNumber.Should().NotBeNull();
        foundByNumber!.Id.Should().Be(txn.Id);
        foundByNumber.Amount.Should().Be(2500m);

        var foundById = await _transactionRepository.GetByIdAsync(txn.Id);
        foundById.Should().NotBeNull();
        foundById!.TransactionNumber.Should().Be(targetTxnNumber);
    }
}
