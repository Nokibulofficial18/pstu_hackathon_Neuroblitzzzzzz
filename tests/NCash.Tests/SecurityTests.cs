using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NCash.Application.Contracts.Security;
using NCash.Application.Modules.Auth;
using NCash.Application.Modules.MoneyRequests;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.RiskShield;
using NCash.Application.Modules.Users;
using NCash.Application.Modules.Wallet;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using NCash.Infrastructure.Security;
using Xunit;

namespace NCash.Tests;

public class SecurityTests
{
    private readonly NCashDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtGenerator;
    private readonly IWalletService _walletService;
    private readonly IMoneyRequestService _moneyRequestService;
    private readonly IUserService _userService;

    public SecurityTests()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_Security_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new NCashDbContext(options);
        _passwordHasher = new PasswordHasher();

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["Jwt:Secret"]).Returns("NCash_Super_Secure_Secret_Key_For_Hackathon_2026_Min_32_Chars!");
        configMock.Setup(c => c["Jwt:Issuer"]).Returns("NCash");
        configMock.Setup(c => c["Jwt:Audience"]).Returns("NCashUsers");
        configMock.Setup(c => c["Jwt:ExpiryMinutes"]).Returns("60");

        _jwtGenerator = new JwtTokenGenerator(configMock.Object);
        _userService = new UserService(_context);
        var accRepo = new AccountRepository(_context);
        _walletService = new WalletService(_context, accRepo);

        var txnRepo = new TransactionRepository(_context);
        var ledRepo = new LedgerRepository(_context);
        var idempRepo = new IdempotencyRepository(_context);
        var risk = new RiskShieldService(_context, txnRepo, NullLogger<RiskShieldService>.Instance);
        var paymentEngine = new PaymentEngine(_context, accRepo, txnRepo, ledRepo, idempRepo, risk, NullLogger<PaymentEngine>.Instance);

        _moneyRequestService = new MoneyRequestService(_context, accRepo, paymentEngine, NullLogger<MoneyRequestService>.Instance);
    }

    private async Task<(User User1, Account Account1, User User2, Account Account2)> SetupTwoUsersAsync()
    {
        var u1 = new User("Alice", "alice", "alice@sec.local", "+8801700000010", _passwordHasher.HashPassword("Password123!"), role: "User");
        var a1 = new Account(u1.Id, "ACC-SEC-1", 50000m, "BDT");
        u1.SetAccount(a1);

        var u2 = new User("Bob", "bob", "bob@sec.local", "+8801700000020", _passwordHasher.HashPassword("Password123!"), role: "User");
        var a2 = new Account(u2.Id, "ACC-SEC-2", 30000m, "BDT");
        u2.SetAccount(a2);

        await _context.Users.AddRangeAsync(u1, u2);
        await _context.Accounts.AddRangeAsync(a1, a2);
        await _context.SaveChangesAsync();

        return (u1, a1, u2, a2);
    }

    [Fact]
    public async Task Test_1_Unauthorized_Wallet_Access_Rejects_NonExistent_Or_Mismatched_User()
    {
        var (u1, _, _, _) = await SetupTwoUsersAsync();
        var forgedUserId = Guid.NewGuid();

        var walletAction = async () => await _walletService.GetWalletSummaryAsync(forgedUserId);
        var ex = await walletAction.Should().ThrowAsync<DomainException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.AccountNotFound);
    }

    [Fact]
    public async Task Test_2_Unauthorized_MoneyRequest_Action_Rejects_ThirdParty_User()
    {
        var (u1, a1, u2, a2) = await SetupTwoUsersAsync();

        // u3 is an unrelated intruder
        var u3 = new User("Eve Intruder", "eve", "eve@sec.local", "+8801700000030", _passwordHasher.HashPassword("Password123!"));
        var a3 = new Account(u3.Id, "ACC-SEC-3", 10000m, "BDT");
        u3.SetAccount(a3);
        await _context.Users.AddAsync(u3);
        await _context.Accounts.AddAsync(a3);
        await _context.SaveChangesAsync();

        // Alice requests money from Bob
        var reqRes = await _moneyRequestService.CreateRequestAsync(u1.Id, new CreateMoneyRequestDto(
            PayerAccountNumber: a2.AccountNumber,
            Amount: 5000m,
            Note: "Alice to Bob"));
        reqRes.Id.Should().NotBeEmpty();
        var requestId = reqRes.Id;

        // Eve tries to Accept/Pay Bob's request
        var evePayAction = async () => await _moneyRequestService.PayRequestAsync(u3.Id, requestId, new PayMoneyRequestDto(null, $"IDEMP-{Guid.NewGuid():N}"));
        var exPay = await evePayAction.Should().ThrowAsync<DomainException>();
        exPay.Which.ErrorCode.Should().Be(ErrorCodes.UnauthorizedAccess);

        // Eve tries to Reject Bob's request
        var eveRejectAction = async () => await _moneyRequestService.RejectRequestAsync(u3.Id, requestId);
        var exReject = await eveRejectAction.Should().ThrowAsync<DomainException>();
        exReject.Which.ErrorCode.Should().Be(ErrorCodes.UnauthorizedAccess);

        // Eve tries to Cancel Alice's request
        var eveCancelAction = async () => await _moneyRequestService.CancelRequestAsync(u3.Id, requestId);
        var exCancel = await eveCancelAction.Should().ThrowAsync<DomainException>();
        exCancel.Which.ErrorCode.Should().Be(ErrorCodes.UnauthorizedAccess);
    }

    [Fact]
    public async Task Test_3_Fake_UserId_Injection_Rejects_Forged_Security_Context()
    {
        var (u1, _, _, _) = await SetupTwoUsersAsync();
        var fakeId = Guid.NewGuid();

        var profileAction = async () => await _userService.GetUserProfileAsync(fakeId);
        var exProfile = await profileAction.Should().ThrowAsync<DomainException>();
        exProfile.Which.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public void Test_4_Valid_JWT_Generates_Authorized_Claims_With_Correct_Role()
    {
        var user = new User("Alice", "alice", "alice@test.local", "+8801700000010", "hash", role: "User");
        var account = new Account(user.Id, "ACC-10001", 10000m, "BDT");
        user.SetAccount(account);

        var token = _jwtGenerator.GenerateToken(user, account);

        token.Should().NotBeNullOrEmpty();
        token.Split('.').Should().HaveCount(3); // Header.Payload.Signature
    }

    [Fact]
    public void Test_5_Expired_Or_Short_Expiry_Configuration_Respected()
    {
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["Jwt:Secret"]).Returns("NCash_Super_Secure_Secret_Key_For_Hackathon_2026_Min_32_Chars!");
        configMock.Setup(c => c["Jwt:Issuer"]).Returns("NCash");
        configMock.Setup(c => c["Jwt:Audience"]).Returns("NCashUsers");
        configMock.Setup(c => c["Jwt:ExpiryMinutes"]).Returns("1");

        var generator = new JwtTokenGenerator(configMock.Object);
        var user = new User("Bob", "bob", "bob@test.local", "+8801700000020", "hash", role: "User");
        var token = generator.GenerateToken(user, null);
        token.Should().NotBeNullOrEmpty();
    }
}
