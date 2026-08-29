using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Contracts.Security;
using NCash.Application.Modules.Auth;
using NCash.Application.Modules.Auth.DTOs;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using NCash.Infrastructure.Security;
using NCash.Infrastructure.Seed;
using Xunit;

namespace NCash.Tests;

public class AuthAndSecurityTests
{
    private readonly NCashDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly IAuthService _authService;

    public AuthAndSecurityTests()
    {
        var options = new DbContextOptionsBuilder<NCashDbContext>()
            .UseInMemoryDatabase(databaseName: $"NCash_Auth_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new NCashDbContext(options);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["Jwt:Secret"]).Returns("NCash_Test_Secret_Key_For_Unit_Tests_32_Chars!");
        configMock.Setup(c => c["Jwt:Issuer"]).Returns("NCashTest");
        configMock.Setup(c => c["Jwt:Audience"]).Returns("NCashTestUsers");
        configMock.Setup(c => c["Jwt:ExpiryMinutes"]).Returns("60");

        _passwordHasher = new PasswordHasher();
        _jwtTokenGenerator = new JwtTokenGenerator(configMock.Object);

        var accountRepository = new AccountRepository(_context);
        var ledgerRepository = new LedgerRepository(_context);
        var transactionRepository = new TransactionRepository(_context);

        // Seed Treasury and Demo Users for test fixture
        DbInitializer.InitializeAsync(_context, _passwordHasher, isDevelopment: true).GetAwaiter().GetResult();

        _authService = new AuthService(
            _context,
            accountRepository,
            ledgerRepository,
            transactionRepository,
            _passwordHasher,
            _jwtTokenGenerator,
            NullLogger<AuthService>.Instance);
    }

    [Fact]
    public async Task Register_ValidRequest_CreatesUserAccountAndFunds100kSimulatedFunds()
    {
        // Arrange
        var request = new RegisterRequestDto(
            "Karim Uddin",
            "karim77",
            "karim@test.local",
            "+8801755555555",
            "SecurePass123!",
            "1234");

        // Act
        var result = await _authService.RegisterAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.Username.Should().Be("karim77");
        result.Balance.Should().Be(100000m);
        result.Token.Should().NotBeNullOrEmpty();
        result.HasPinConfigured.Should().BeTrue();

        // Verify in DB
        var user = await _context.Users.Include(u => u.Account).FirstOrDefaultAsync(u => u.Username == "karim77");
        user.Should().NotBeNull();
        user!.Account.Should().NotBeNull();
        user.Account!.Balance.Should().Be(100000m);
        user.PasswordHash.Should().NotBe("SecurePass123!"); // Never plaintext
        _passwordHasher.VerifyPassword("SecurePass123!", user.PasswordHash).Should().BeTrue();

        // Verify Double-Entry Ledger for initial system issuance
        var ledgerEntries = await _context.LedgerEntries.Where(l => l.AccountId == user.Account.Id).ToListAsync();
        ledgerEntries.Should().HaveCount(1);
        ledgerEntries[0].Direction.Should().Be(LedgerDirection.Credit);
        ledgerEntries[0].Amount.Should().Be(100000m);
        ledgerEntries[0].BalanceAfter.Should().Be(100000m);
    }

    [Fact]
    public async Task Register_DuplicateUsername_ThrowsDomainException()
    {
        // Arrange: Rahim already seeded in DbInitializer
        var request = new RegisterRequestDto(
            "Rahim Duplicate",
            "rahim",
            "rahim_new@test.local",
            "+8801799999999",
            "Password123!");

        // Act
        Func<Task> act = async () => await _authService.RegisterAsync(request);

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.UserAlreadyExists);
    }

    [Fact]
    public async Task Register_DuplicateEmail_ThrowsDomainException()
    {
        // Arrange
        var request = new RegisterRequestDto(
            "Tasir Clone",
            "tasir_unique",
            "tasir@example.com", // Already in seed
            "+8801788888888",
            "Password123!");

        // Act
        Func<Task> act = async () => await _authService.RegisterAsync(request);

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.UserAlreadyExists);
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsJwtTokenAndUserAccount()
    {
        // Arrange
        var request = new LoginRequestDto("rahim", "Password123!");

        // Act
        var result = await _authService.LoginAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.Username.Should().Be("rahim");
        result.Token.Should().NotBeNullOrEmpty();
        result.AccountNumber.Should().Be("ACC-100001");
    }

    [Fact]
    public async Task Login_InvalidPassword_Throws401AntiEnumeration()
    {
        // Arrange
        var request = new LoginRequestDto("rahim", "WrongPassword!");

        // Act
        Func<Task> act = async () => await _authService.LoginAsync(request);

        // Assert
        var ex = await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidCredentials);
        ex.Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task Login_NonExistentUser_Throws401AntiEnumeration()
    {
        // Arrange
        var request = new LoginRequestDto("nonexistent_user_99", "SomePassword!");

        // Act
        Func<Task> act = async () => await _authService.LoginAsync(request);

        // Assert
        var ex = await act.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidCredentials);
        ex.Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task SetAndVerify_TransactionPin_ValidatesCorrectlyAndRejectsWrongPin()
    {
        // Arrange: Register a user with unique phone
        var reg = await _authService.RegisterAsync(new RegisterRequestDto("PIN Tester", "pintester", "pin@test.local", "+8801744444499", "Pass123!"));
        var userId = reg.UserId;

        // Act 1: Set PIN to 5892
        var setResult = await _authService.SetTransactionPinAsync(userId, new SetPinRequestDto("5892", "5892"));
        setResult.Success.Should().BeTrue();

        // Act 2: Verify correct PIN
        var verifyOk = await _authService.VerifyTransactionPinAsync(userId, new VerifyPinRequestDto("5892"));
        verifyOk.Success.Should().BeTrue();

        // Act 3: Verify wrong PIN -> Throws 401
        Func<Task> verifyBad = async () => await _authService.VerifyTransactionPinAsync(userId, new VerifyPinRequestDto("9999"));
        await verifyBad.Should().ThrowAsync<DomainException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidCredentials);
    }

    [Fact]
    public async Task GetCurrentUser_ValidUserId_ReturnsFullProfile()
    {
        // Arrange: Use seeded user Rahim
        var user = await _context.Users.FirstAsync(u => u.Username == "rahim");

        // Act
        var profile = await _authService.GetCurrentUserAsync(user.Id);

        // Assert
        profile.Should().NotBeNull();
        profile.Username.Should().Be("rahim");
        profile.Email.Should().Be("rahim@example.com");
        profile.AccountNumber.Should().Be("ACC-100001");
        profile.Balance.Should().Be(100000m);
    }
}
