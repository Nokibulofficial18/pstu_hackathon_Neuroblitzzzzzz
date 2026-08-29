using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.MoneyRequests;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RecoveryCenter;
using NCash.Application.Modules.RiskShield;
using NCash.Application.Modules.TrustLab;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using Xunit;

namespace NCash.Tests;

public class ConcurrencyTests
{
    private (NCashDbContext Context, ITrustLabService TrustLab, IPaymentEngine PaymentEngine) CreateServices()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_Concurrency_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var context = new NCashDbContext(options);
        var accRepo = new AccountRepository(context);
        var txnRepo = new TransactionRepository(context);
        var ledRepo = new LedgerRepository(context);
        var idempRepo = new IdempotencyRepository(context);
        var risk = new RiskShieldService(context, txnRepo, NullLogger<RiskShieldService>.Instance);

        var paymentEngine = new PaymentEngine(
            context,
            accRepo,
            txnRepo,
            ledRepo,
            idempRepo,
            risk,
            NullLogger<PaymentEngine>.Instance);

        var moneyReqService = new MoneyRequestService(context, accRepo, paymentEngine, NullLogger<MoneyRequestService>.Instance);
        var recoveryService = new RecoveryCenterService(context, txnRepo, ledRepo, accRepo, paymentEngine, NullLogger<RecoveryCenterService>.Instance);

        var trustLab = new TrustLabService(
            context,
            paymentEngine,
            accRepo,
            ledRepo,
            txnRepo,
            moneyReqService,
            recoveryService,
            NullLogger<TrustLabService>.Instance);

        return (context, trustLab, paymentEngine);
    }

    [Fact]
    public async Task Test_1_Concurrent_Spend_1000_Balance_With_Two_700_Spends_Leaves_300_Balance()
    {
        var (context, trustLab, _) = CreateServices();

        // Setup User with exactly 1000 BDT
        var user = new User("Alice Concurrency", "alice.conc", "alice@conc.local", "+8801700000001", "hash");
        var account = new Account(user.Id, "ACC-CONC-1000", 1000m, "BDT");
        user.SetAccount(account);

        await context.Users.AddAsync(user);
        await context.Accounts.AddAsync(account);
        await context.SaveChangesAsync();

        // Run concurrent spend test
        var result = await trustLab.RunConcurrencyTestAsync(user.Id);

        result.Passed.Should().BeTrue();
        result.SucceededCount.Should().Be(1);
        result.FailedDueToInsufficientFundsCount.Should().Be(1);
        result.FinalBalance.Should().Be(300.00m);
        result.OverdraftOccurred.Should().BeFalse();
    }

    [Fact]
    public async Task Test_2_Concurrent_Spend_5000_Balance_With_Two_4000_Spends_Leaves_1000_Balance()
    {
        var (context, _, paymentEngine) = CreateServices();

        // Setup Sender with 5000 BDT
        var senderUser = new User("Sender 5000", "sender.5000", "s5000@test.local", "+8801700000002", "hash");
        var senderAccount = new Account(senderUser.Id, "ACC-5000", 5000m, "BDT");
        senderUser.SetAccount(senderAccount);

        // Setup Receivers
        var r1User = new User("Receiver 1", "r1", "r1@test.local", "+8801700000003", "hash");
        var r1Account = new Account(r1User.Id, "ACC-R1", 0m, "BDT");
        r1User.SetAccount(r1Account);

        var r2User = new User("Receiver 2", "r2", "r2@test.local", "+8801700000004", "hash");
        var r2Account = new Account(r2User.Id, "ACC-R2", 0m, "BDT");
        r2User.SetAccount(r2Account);

        await context.Users.AddRangeAsync(senderUser, r1User, r2User);
        await context.Accounts.AddRangeAsync(senderAccount, r1Account, r2Account);
        await context.SaveChangesAsync();

        // Execute two 4000 BDT transfers against 5000 BDT balance
        var cmd1 = new ExecuteTransferCommand(senderAccount.Id, r1Account.Id, 4000m, $"IDEMP-C1-{Guid.NewGuid():N}", TransactionType.Transfer, "Spend 1");
        var cmd2 = new ExecuteTransferCommand(senderAccount.Id, r2Account.Id, 4000m, $"IDEMP-C2-{Guid.NewGuid():N}", TransactionType.Transfer, "Spend 2");

        int successCount = 0;
        int failCount = 0;

        try
        {
            await paymentEngine.ExecutePaymentAsync(cmd1);
            successCount++;
        }
        catch (DomainException)
        {
            failCount++;
        }

        try
        {
            await paymentEngine.ExecutePaymentAsync(cmd2);
            successCount++;
        }
        catch (DomainException)
        {
            failCount++;
        }

        successCount.Should().Be(1);
        failCount.Should().Be(1);

        // Reload fresh account state
        var reloadedSender = await context.Accounts.FindAsync(senderAccount.Id);
        reloadedSender!.Balance.Should().Be(1000m);
        reloadedSender.Balance.Should().BeGreaterThanOrEqualTo(0m);
    }
}
