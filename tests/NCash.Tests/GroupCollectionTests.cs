using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Modules.GroupCollect;
using NCash.Application.Modules.GroupCollect.DTOs;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.RiskShield;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using Xunit;

namespace NCash.Tests;

public class GroupCollectionTests
{
    private readonly NCashDbContext _context;
    private readonly IAccountRepository _accountRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ILedgerRepository _ledgerRepository;
    private readonly IIdempotencyRepository _idempotencyRepository;
    private readonly IRiskShieldService _riskShieldService;
    private readonly IPaymentEngine _paymentEngine;
    private readonly IGroupCollectService _groupCollectService;

    public GroupCollectionTests()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_GroupColl_{Guid.NewGuid():N}")
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

        _groupCollectService = new GroupCollectService(
            _context,
            _accountRepository,
            _paymentEngine,
            NullLogger<GroupCollectService>.Instance);
    }

    private async Task<(User Creator, Account CreatorAcc, User Member1, Account Member1Acc, User Member2, Account Member2Acc)> SeedUsersAsync()
    {
        var creator = new User("Creator User", "creator", "creator@test.local", "+8801700000010", "pass");
        var creatorAcc = new Account(creator.Id, "ACC-CREATOR", 1000m, "BDT");
        creator.SetAccount(creatorAcc);

        var m1 = new User("Saif Ahmed", "saif", "saif@test.local", "+8801700000011", "pass");
        var m1Acc = new Account(m1.Id, "ACC-SAIF", 5000m, "BDT");
        m1.SetAccount(m1Acc);

        var m2 = new User("Hasan Ali", "hasan", "hasan@test.local", "+8801700000012", "pass");
        var m2Acc = new Account(m2.Id, "ACC-HASAN", 5000m, "BDT");
        m2.SetAccount(m2Acc);

        await _context.Users.AddRangeAsync(creator, m1, m2);
        await _context.Accounts.AddRangeAsync(creatorAcc, m1Acc, m2Acc);
        await _context.SaveChangesAsync();

        return (creator, creatorAcc, m1, m1Acc, m2, m2Acc);
    }

    [Fact]
    public async Task GroupCollection_CreateCollection_WithMembers_SuccessfullyInitializes()
    {
        // Arrange
        var (creator, _, m1, m1Acc, m2, m2Acc) = await SeedUsersAsync();

        var createDto = new CreateGroupCollectionDto(
            "Trip Expense",
            "Sajek Valley Trip Shared Pool",
            4000m,
            14,
            [
                new MemberInvitationDto(m1Acc.AccountNumber, 2000m),
                new MemberInvitationDto(m2Acc.AccountNumber, 2000m)
            ]);

        // Act
        var result = await _groupCollectService.CreateCollectionAsync(creator.Id, createDto);

        // Assert
        result.Should().NotBeNull();
        result.Title.Should().Be("Trip Expense");
        result.TargetAmount.Should().Be(4000m);
        result.CollectedAmount.Should().Be(0m);
        result.RemainingAmount.Should().Be(4000m);
        result.Status.Should().Be("Pending");
        result.Members.Should().HaveCount(2);
        result.Members.All(m => m.Status == "Pending").Should().BeTrue();
    }

    [Fact]
    public async Task GroupCollection_InviteMember_AddsNewMemberWithRequiredAmount()
    {
        // Arrange
        var (creator, _, m1, m1Acc, _, _) = await SeedUsersAsync();
        var collection = await _groupCollectService.CreateCollectionAsync(creator.Id, new CreateGroupCollectionDto("Dinner Pool", "Team Dinner", 3000m));

        // Act
        var result = await _groupCollectService.InviteMemberAsync(creator.Id, collection.Id, new InviteMemberRequestDto(m1Acc.AccountNumber, 1500m));

        // Assert
        result.Members.Should().HaveCount(1);
        result.Members[0].AccountNumber.Should().Be(m1Acc.AccountNumber);
        result.Members[0].RequiredAmount.Should().Be(1500m);
        result.Members[0].PaidAmount.Should().Be(0m);
        result.Members[0].Status.Should().Be("Pending");
    }

    [Fact]
    public async Task GroupCollection_PayContribution_PartialAndFull_UsesPaymentEngineAndTransitionsStatus()
    {
        // Arrange: Trip Expense = 4000 BDT (Saif: 2000, Hasan: 2000)
        var (creator, creatorAcc, m1, m1Acc, m2, m2Acc) = await SeedUsersAsync();
        var collection = await _groupCollectService.CreateCollectionAsync(creator.Id, new CreateGroupCollectionDto(
            "Trip Expense",
            "Sajek Tour Pool",
            4000m,
            14,
            [
                new MemberInvitationDto(m1Acc.AccountNumber, 2000m),
                new MemberInvitationDto(m2Acc.AccountNumber, 2000m)
            ]));

        // Act 1: Saif pays partial contribution of 1000 BDT
        var payResult1 = await _groupCollectService.PayContributionAsync(m1.Id, collection.Id, new PayContributionDto(1000m, "KEY-CONTRIB-1"));
        payResult1.Status.Should().Be("Succeeded");

        var col1 = await _groupCollectService.GetCollectionByIdAsync(creator.Id, collection.Id);
        col1.CollectedAmount.Should().Be(1000m);
        col1.RemainingAmount.Should().Be(3000m);
        col1.Status.Should().Be("PartiallyPaid");

        var saifMember1 = col1.Members.First(m => m.UserId == m1.Id);
        saifMember1.PaidAmount.Should().Be(1000m);
        saifMember1.RemainingAmount.Should().Be(1000m);
        saifMember1.Status.Should().Be("PartiallyPaid");

        // Act 2: Saif pays remaining 1000 BDT
        await _groupCollectService.PayContributionAsync(m1.Id, collection.Id, new PayContributionDto(1000m, "KEY-CONTRIB-2"));

        var col2 = await _groupCollectService.GetCollectionByIdAsync(creator.Id, collection.Id);
        var saifMember2 = col2.Members.First(m => m.UserId == m1.Id);
        saifMember2.PaidAmount.Should().Be(2000m);
        saifMember2.RemainingAmount.Should().Be(0m);
        saifMember2.Status.Should().Be("Paid");

        // Act 3: Hasan pays full 2000 BDT
        await _groupCollectService.PayContributionAsync(m2.Id, collection.Id, new PayContributionDto(2000m, "KEY-CONTRIB-3"));

        var colFinal = await _groupCollectService.GetCollectionByIdAsync(creator.Id, collection.Id);
        colFinal.CollectedAmount.Should().Be(4000m);
        colFinal.RemainingAmount.Should().Be(0m);
        colFinal.Status.Should().Be("Paid");
        colFinal.Members.All(m => m.Status == "Paid").Should().BeTrue();

        // Verify creator received funds
        var refreshedCreatorAcc = await _accountRepository.GetByIdAsync(creatorAcc.Id);
        refreshedCreatorAcc!.Balance.Should().Be(5000m); // 1000 initial + 4000 collected
    }

    [Fact]
    public async Task GroupCollection_PayContribution_ExceedingRequiredAmount_ThrowsDomainException()
    {
        // Arrange
        var (creator, _, m1, m1Acc, _, _) = await SeedUsersAsync();
        var collection = await _groupCollectService.CreateCollectionAsync(creator.Id, new CreateGroupCollectionDto(
            "Dinner Pool",
            "Dinner",
            2000m,
            14,
            [new MemberInvitationDto(m1Acc.AccountNumber, 1000m)]));

        // Act: Saif attempts to pay 1500 BDT when required is 1000 BDT
        var act = async () => await _groupCollectService.PayContributionAsync(m1.Id, collection.Id, new PayContributionDto(1500m, "KEY-EXCESS"));

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidAmount);
    }

    [Fact]
    public async Task GroupCollection_CancelCollection_ByCreator_CancelsSuccessfully()
    {
        // Arrange
        var (creator, _, _, _, _, _) = await SeedUsersAsync();
        var collection = await _groupCollectService.CreateCollectionAsync(creator.Id, new CreateGroupCollectionDto("Cancelled Pool", "Pool", 2000m));

        // Act
        var result = await _groupCollectService.CancelCollectionAsync(creator.Id, collection.Id);

        // Assert
        result.Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task GroupCollection_CancelCollection_ByNonCreator_ThrowsUnauthorized()
    {
        // Arrange
        var (creator, _, m1, _, _, _) = await SeedUsersAsync();
        var collection = await _groupCollectService.CreateCollectionAsync(creator.Id, new CreateGroupCollectionDto("Pool", "Pool", 2000m));

        // Act
        var act = async () => await _groupCollectService.CancelCollectionAsync(m1.Id, collection.Id);

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.UnauthorizedAccess);
    }

    [Fact]
    public void GroupCollection_InvalidTargetAmount_ThrowsDomainException()
    {
        // Act
        Action act = () => new GroupCollection(Guid.NewGuid(), Guid.NewGuid(), "Invalid", "Desc", -100m);

        // Assert
        act.Should().Throw<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidAmount);
    }
}
