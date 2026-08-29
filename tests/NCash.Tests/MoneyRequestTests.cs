using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.MoneyRequests;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.RiskShield;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using Xunit;

namespace NCash.Tests;

public class MoneyRequestTests
{
    private readonly NCashDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly IIdempotencyRepository _idempotencyRepository;
    private readonly IRiskShieldService _riskShieldService;
    private readonly IPaymentEngine _paymentEngine;
    private readonly IMoneyRequestService _requestService;

    public MoneyRequestTests()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_Req_{Guid.NewGuid():N}")
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

        _requestService = new MoneyRequestService(
            _context,
            _accountRepository,
            _paymentEngine,
            NullLogger<MoneyRequestService>.Instance);
    }

    private async Task<(User Requester, Account ReqAccount, User Payer, Account PayerAccount)> SeedUsersAsync(decimal payerBalance = 10000m)
    {
        var u1 = new User("Requester Alice", "alice", "alice@req.local", "+8801700000010", "pass");
        var a1 = new Account(u1.Id, "ACC-REQ-01", 1000m, "BDT");
        u1.SetAccount(a1);

        var u2 = new User("Payer Bob", "bob", "bob@req.local", "+8801700000020", "pass");
        var a2 = new Account(u2.Id, "ACC-PAYER-01", payerBalance, "BDT");
        u2.SetAccount(a2);

        await _context.Users.AddRangeAsync(u1, u2);
        await _context.Accounts.AddRangeAsync(a1, a2);
        await _context.SaveChangesAsync();

        return (u1, a1, u2, a2);
    }

    [Fact]
    public async Task CreateMoneyRequest_ValidPayer_CreatesPendingRequest()
    {
        // Arrange
        var (alice, _, _, bobAcc) = await SeedUsersAsync();
        var dto = new CreateMoneyRequestDto(bobAcc.AccountNumber, 5000m, "Split hotel expense", 7);

        // Act
        var result = await _requestService.CreateRequestAsync(alice.Id, dto);

        // Assert
        result.Should().NotBeNull();
        result.Amount.Should().Be(5000m);
        result.PaidAmount.Should().Be(0m);
        result.RemainingAmount.Should().Be(5000m);
        result.Status.Should().Be("Pending");
        result.Note.Should().Be("Split hotel expense");
    }

    [Fact]
    public async Task CreateMoneyRequest_SelfRequest_ThrowsDomainException()
    {
        // Arrange
        var (alice, aliceAcc, _, _) = await SeedUsersAsync();
        var dto = new CreateMoneyRequestDto(aliceAcc.AccountNumber, 1000m, "Self request");

        // Act
        Func<Task> act = async () => await _requestService.CreateRequestAsync(alice.Id, dto);

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.SelfTransferNotAllowed);
    }

    [Fact]
    public async Task PayMoneyRequest_FullAmount_ExecutesPaymentAndMarksPaid()
    {
        // Arrange
        var (alice, _, bob, bobAcc) = await SeedUsersAsync(10000m);
        var req = await _requestService.CreateRequestAsync(alice.Id, new CreateMoneyRequestDto(bobAcc.AccountNumber, 3000m, "Full payment test"));
        var payDto = new PayMoneyRequestDto(PaymentAmount: null, IdempotencyKey: $"IDEMP-PAY-{Guid.NewGuid():N}");

        // Act
        var payResult = await _requestService.PayRequestAsync(bob.Id, req.Id, payDto);

        // Assert
        payResult.Should().NotBeNull();
        payResult.Status.Should().Be("Succeeded");
        payResult.Amount.Should().Be(3000m);
        payResult.SenderNewBalance.Should().Be(7000m);
        payResult.ReceiverNewBalance.Should().Be(4000m);

        // Verify request updated in DB
        var updatedReq = await _context.MoneyRequests.FindAsync(req.Id);
        updatedReq!.Status.Should().Be(MoneyRequestStatus.Paid);
        updatedReq.PaidAmount.Should().Be(3000m);
        updatedReq.RemainingAmount.Should().Be(0m);
    }

    [Fact]
    public async Task PayMoneyRequest_PartialAmount_ThenRemaining_TracksStatusProgression()
    {
        // Scenario: Request = 5000. Pay 2000 -> PartiallyPaid (Remaining 3000). Pay 3000 -> Paid (Remaining 0).
        // Arrange
        var (alice, _, bob, bobAcc) = await SeedUsersAsync(10000m);
        var req = await _requestService.CreateRequestAsync(alice.Id, new CreateMoneyRequestDto(bobAcc.AccountNumber, 5000m, "Partial split"));

        // Act 1: Partial payment of 2000
        var pay1 = new PayMoneyRequestDto(PaymentAmount: 2000m, IdempotencyKey: $"IDEMP-PART1-{Guid.NewGuid():N}");
        var res1 = await _requestService.PayRequestAsync(bob.Id, req.Id, pay1);
        res1.Status.Should().Be("Succeeded");

        var reqAfterPart1 = await _context.MoneyRequests.FindAsync(req.Id);
        reqAfterPart1!.Status.Should().Be(MoneyRequestStatus.PartiallyPaid);
        reqAfterPart1.PaidAmount.Should().Be(2000m);
        reqAfterPart1.RemainingAmount.Should().Be(3000m);

        // Act 2: Remaining payment of 3000
        var pay2 = new PayMoneyRequestDto(PaymentAmount: 3000m, IdempotencyKey: $"IDEMP-PART2-{Guid.NewGuid():N}");
        var res2 = await _requestService.PayRequestAsync(bob.Id, req.Id, pay2);
        res2.Status.Should().Be("Succeeded");

        var reqAfterPart2 = await _context.MoneyRequests.FindAsync(req.Id);
        reqAfterPart2!.Status.Should().Be(MoneyRequestStatus.Paid);
        reqAfterPart2.PaidAmount.Should().Be(5000m);
        reqAfterPart2.RemainingAmount.Should().Be(0m);

        // Verify balances: Bob paid 2000 + 3000 = 5000 (10000 - 5000 = 5000). Alice received 5000 (1000 + 5000 = 6000).
        var freshAliceAcc = await _context.Accounts.FindAsync(alice.Account.Id);
        var freshBobAcc = await _context.Accounts.FindAsync(bobAcc.Id);
        freshAliceAcc!.Balance.Should().Be(6000m);
        freshBobAcc!.Balance.Should().Be(5000m);
    }

    [Fact]
    public async Task PayMoneyRequest_Overpayment_ThrowsDomainException()
    {
        // Arrange
        var (alice, _, bob, bobAcc) = await SeedUsersAsync(10000m);
        var req = await _requestService.CreateRequestAsync(alice.Id, new CreateMoneyRequestDto(bobAcc.AccountNumber, 2000m, "Strict amount"));
        var payDto = new PayMoneyRequestDto(PaymentAmount: 5000m, IdempotencyKey: $"IDEMP-{Guid.NewGuid():N}"); // 5000 > 2000

        // Act
        Func<Task> act = async () => await _requestService.PayRequestAsync(bob.Id, req.Id, payDto);

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.MoneyRequestInvalidAmount);
    }

    [Fact]
    public async Task PayMoneyRequest_Expired_ThrowsDomainException()
    {
        // Arrange
        var (alice, aliceAcc, bob, bobAcc) = await SeedUsersAsync();
        var expiredReq = new MoneyRequest(aliceAcc.Id, bobAcc.Id, 1000m, "Expired request", DateTime.UtcNow.AddMinutes(-5));
        await _context.MoneyRequests.AddAsync(expiredReq);
        await _context.SaveChangesAsync();

        var payDto = new PayMoneyRequestDto(PaymentAmount: null, IdempotencyKey: $"IDEMP-{Guid.NewGuid():N}");

        // Act
        Func<Task> act = async () => await _requestService.PayRequestAsync(bob.Id, expiredReq.Id, payDto);

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.MoneyRequestExpired);
    }

    [Fact]
    public async Task RejectMoneyRequest_Payer_MarksRejectedAndBlocksSubsequentPayment()
    {
        // Arrange
        var (alice, _, bob, bobAcc) = await SeedUsersAsync();
        var req = await _requestService.CreateRequestAsync(alice.Id, new CreateMoneyRequestDto(bobAcc.AccountNumber, 1500m, "Reject test"));

        // Act 1: Reject request
        var rejectResult = await _requestService.RejectRequestAsync(bob.Id, req.Id);
        rejectResult.Status.Should().Be("Rejected");

        // Act 2: Attempting payment after rejection must fail
        var payDto = new PayMoneyRequestDto(PaymentAmount: null, IdempotencyKey: $"IDEMP-{Guid.NewGuid():N}");
        Func<Task> actPay = async () => await _requestService.PayRequestAsync(bob.Id, req.Id, payDto);

        await actPay.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.MoneyRequestAlreadyClosed);
    }

    [Fact]
    public async Task CancelMoneyRequest_Requester_MarksCancelled()
    {
        // Arrange
        var (alice, _, _, bobAcc) = await SeedUsersAsync();
        var req = await _requestService.CreateRequestAsync(alice.Id, new CreateMoneyRequestDto(bobAcc.AccountNumber, 1200m, "Cancel test"));

        // Act
        var cancelResult = await _requestService.CancelRequestAsync(alice.Id, req.Id);

        // Assert
        cancelResult.Status.Should().Be("Cancelled");

        var updatedReq = await _context.MoneyRequests.FindAsync(req.Id);
        updatedReq!.Status.Should().Be(MoneyRequestStatus.Cancelled);
    }
}
