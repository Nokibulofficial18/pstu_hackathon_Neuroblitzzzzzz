using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.MoneyRequests;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.PaymentEngine.DTOs;
using NCash.Application.Modules.RiskShield;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using Xunit;

namespace NCash.Tests;

public class IdempotencyTests
{
    private readonly NCashDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly IIdempotencyRepository _idempotencyRepository;
    private readonly IRiskShieldService _riskShieldService;
    private readonly IPaymentEngine _paymentEngine;
    private readonly IMoneyRequestService _moneyRequestService;

    public IdempotencyTests()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_Idempotency_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new NCashDbContext(options);
        _accountRepository = new AccountRepository(_context);
        _transactionRepository = new TransactionRepository(_context);
        _ledgerRepository = new LedgerRepository(_context);
        _idempotencyRepository = new IdempotencyRepository(_context);
        _riskShieldService = new RiskShieldService(_context, _transactionRepository, NullLogger<RiskShieldService>.Instance);

        _paymentEngine = new PaymentEngine(
            _context,
            _accountRepository,
            _transactionRepository,
            _ledgerRepository,
            _idempotencyRepository,
            _riskShieldService,
            NullLogger<PaymentEngine>.Instance);

        _moneyRequestService = new MoneyRequestService(
            _context,
            _accountRepository,
            _paymentEngine,
            NullLogger<MoneyRequestService>.Instance);
    }

    private async Task<(User Sender, Account SenderAcc, User Receiver, Account ReceiverAcc)> SetupAccountsAsync(decimal senderBalance = 10000m, decimal receiverBalance = 2000m)
    {
        var senderUser = new User("Alice Sender", "alice", "alice@test.local", "+8801711111111", "hash");
        var senderAccount = new Account(senderUser.Id, $"ACC-{Guid.NewGuid().ToString("N")[..6]}", senderBalance, "BDT");
        senderUser.SetAccount(senderAccount);

        var receiverUser = new User("Bob Receiver", "bob", "bob@test.local", "+8801722222222", "hash");
        var receiverAccount = new Account(receiverUser.Id, $"ACC-{Guid.NewGuid().ToString("N")[..6]}", receiverBalance, "BDT");
        receiverUser.SetAccount(receiverAccount);

        await _context.Users.AddRangeAsync(senderUser, receiverUser);
        await _context.Accounts.AddRangeAsync(senderAccount, receiverAccount);
        await _context.SaveChangesAsync();

        return (senderUser, senderAccount, receiverUser, receiverAccount);
    }

    [Fact]
    public async Task Test_1_Same_Idempotency_Key_Twice_Returns_Cached_Result_With_Single_Financial_Effect()
    {
        var (_, senderAcc, _, receiverAcc) = await SetupAccountsAsync(10000m, 2000m);
        var sharedKey = $"IDEMP-{Guid.NewGuid():N}";

        var command = new ExecuteTransferCommand(
            SenderAccountId: senderAcc.Id,
            ReceiverAccountId: receiverAcc.Id,
            Amount: 2500m,
            IdempotencyKey: sharedKey,
            Type: TransactionType.Transfer,
            Purpose: "Lunch bill");

        // Attempt 1: New transaction executed
        var result1 = await _paymentEngine.ExecutePaymentAsync(command);
        result1.IsCached.Should().BeFalse();

        // Attempt 2: Duplicate request using identical key
        var result2 = await _paymentEngine.ExecutePaymentAsync(command);
        result2.IsCached.Should().BeTrue();
        result2.TransactionId.Should().Be(result1.TransactionId);

        // Verification: Exactly 1 debit, exactly 1 credit
        senderAcc.Balance.Should().Be(7500m);
        receiverAcc.Balance.Should().Be(4500m);

        var totalTxns = await _context.Transactions.CountAsync(t => t.IdempotencyKey == sharedKey);
        totalTxns.Should().Be(1);

        var totalLedgerEntries = await _context.LedgerEntries.CountAsync(l => l.TransactionId == result1.TransactionId);
        totalLedgerEntries.Should().Be(2); // 1 Debit, 1 Credit
    }

    [Fact]
    public async Task Test_2_Same_Idempotency_Key_5_Times_Blocks_4_Duplicates_With_Exact_One_Debit_Credit()
    {
        var (_, senderAcc, _, receiverAcc) = await SetupAccountsAsync(10000m, 2000m);
        var sharedKey = $"IDEMP-5X-{Guid.NewGuid():N}";

        var command = new ExecuteTransferCommand(
            SenderAccountId: senderAcc.Id,
            ReceiverAccountId: receiverAcc.Id,
            Amount: 1500m,
            IdempotencyKey: sharedKey,
            Type: TransactionType.Transfer,
            Purpose: "Parallel Retries Test");

        int successfulFinancialEffects = 0;
        int duplicateBlocks = 0;
        Guid? rootTxnId = null;

        for (int i = 0; i < 5; i++)
        {
            var res = await _paymentEngine.ExecutePaymentAsync(command);

            if (!res.IsCached)
            {
                successfulFinancialEffects++;
                rootTxnId = res.TransactionId;
            }
            else
            {
                duplicateBlocks++;
                res.TransactionId.Should().Be(rootTxnId!.Value);
            }
        }

        successfulFinancialEffects.Should().Be(1);
        duplicateBlocks.Should().Be(4);

        senderAcc.Balance.Should().Be(8500m);
        receiverAcc.Balance.Should().Be(3500m);

        var txnCount = await _context.Transactions.CountAsync(t => t.IdempotencyKey == sharedKey);
        txnCount.Should().Be(1);
    }

    [Fact]
    public async Task Test_3_Retry_After_Simulated_Timeout_Returns_Original_Committed_State()
    {
        var (_, senderAcc, _, receiverAcc) = await SetupAccountsAsync(10000m, 2000m);
        var sharedKey = $"TIMEOUT-RETRY-{Guid.NewGuid():N}";

        var command = new ExecuteTransferCommand(
            SenderAccountId: senderAcc.Id,
            ReceiverAccountId: receiverAcc.Id,
            Amount: 3000m,
            IdempotencyKey: sharedKey,
            Type: TransactionType.Transfer,
            Purpose: "Network timeout simulation");

        // Initial execution completes in backend
        var initialRes = await _paymentEngine.ExecutePaymentAsync(command);

        // Client experienced timeout and retries with same idempotency key
        var retryRes = await _paymentEngine.ExecutePaymentAsync(command);
        retryRes.IsCached.Should().BeTrue();
        retryRes.Status.Should().Be("Succeeded");
        retryRes.TransactionNumber.Should().Be(initialRes.TransactionNumber);

        // Balances debited once only
        senderAcc.Balance.Should().Be(7000m);
        receiverAcc.Balance.Should().Be(5000m);
    }

    [Fact]
    public async Task Test_4_Duplicate_MoneyRequest_Payment_Rejected_And_Prevented()
    {
        var (senderUser, senderAcc, receiverUser, receiverAcc) = await SetupAccountsAsync(10000m, 2000m);

        // Bob requests 4000 BDT from Alice
        var createRes = await _moneyRequestService.CreateRequestAsync(receiverUser.Id, new CreateMoneyRequestDto(
            PayerAccountNumber: senderAcc.AccountNumber,
            Amount: 4000m,
            Note: "Dinner expense"));
        createRes.Id.Should().NotBeEmpty();
        var reqId = createRes.Id;

        // Alice pays full amount
        var payKey1 = $"PAY-REQ-{Guid.NewGuid():N}";
        var payRes1 = await _moneyRequestService.PayRequestAsync(senderUser.Id, reqId, new PayMoneyRequestDto(null, payKey1));
        payRes1.Status.Should().Be("Succeeded");
        senderAcc.Balance.Should().Be(6000m);
        receiverAcc.Balance.Should().Be(6000m);

        // Duplicate payment attempt on already paid request
        var payKey2 = $"PAY-REQ-{Guid.NewGuid():N}";
        var duplicateAction = async () => await _moneyRequestService.PayRequestAsync(senderUser.Id, reqId, new PayMoneyRequestDto(null, payKey2));
        await duplicateAction.Should().ThrowAsync<DomainException>();

        // Balances remain strictly untouched
        senderAcc.Balance.Should().Be(6000m);
        receiverAcc.Balance.Should().Be(6000m);
    }
}
