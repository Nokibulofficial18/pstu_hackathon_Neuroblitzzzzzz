using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NCash.Application.Contracts.Security;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;

namespace NCash.Infrastructure.Seed;

public static class DbInitializer
{
    /// <summary>
    /// Initializes the database. In production: runs migrations only, no seeding.
    /// In Development/Test: also seeds demo users and auditor account.
    /// </summary>
    public static async Task InitializeAsync(
        NCashDbContext context,
        IPasswordHasher passwordHasher,
        bool isDevelopment = false,
        ILogger? logger = null)
    {
        // ── P0 FIX: Never silently convert migration failure to EnsureCreated ─────────
        // Migration errors must surface so the application can fail correctly.
        if (context.Database.IsRelational())
        {
            try
            {
                await context.Database.MigrateAsync();
                logger?.LogInformation("NCash database migrations applied successfully.");
            }
            catch (Exception ex)
            {
                logger?.LogCritical(ex,
                    "FATAL: Database migration failed. Application cannot start safely. " +
                    "Check the connection string, database availability, and migration state.");
                // Re-throw — do not silently fall back to EnsureCreated in production
                throw;
            }
        }
        else
        {
            // InMemory (Development/Test only) — EnsureCreated is appropriate here
            await context.Database.EnsureCreatedAsync();
        }

        // ── P0 FIX: Always seed Treasury (it is a system requirement, not a demo user) ─
        await SeedTreasuryAsync(context, passwordHasher, logger);

        // ── P0 FIX: Demo/test user seeding is STRICTLY gated behind isDevelopment ──────
        // This will NEVER run in production.
        if (isDevelopment)
        {
            await SeedDemoUsersAsync(context, passwordHasher, logger);
            await SeedAuditorAsync(context, passwordHasher, logger);
        }
        else
        {
            logger?.LogInformation(
                "Production mode: Skipping demo user and auditor seeding. " +
                "To create an auditor or admin, use a secure provisioning flow.");
        }
    }

    private static async Task SeedTreasuryAsync(NCashDbContext context, IPasswordHasher passwordHasher, ILogger? logger)
    {
        if (await context.Accounts.AnyAsync(a => a.Id == SystemConstants.TreasuryAccountId))
            return;

        // Treasury uses a cryptographically random password — never hard-coded
        var treasuryUser = new User(
            "N-Cash System Treasury",
            "system.treasury",
            "treasury@ncash.internal",
            "+8801700000000",
            passwordHasher.HashPassword(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")),
            role: "System");

        typeof(User).GetProperty(nameof(User.Id))!.SetValue(treasuryUser, SystemConstants.TreasuryUserId);

        var treasuryAccount = new Account(
            SystemConstants.TreasuryUserId,
            SystemConstants.TreasuryAccountNumber,
            1000000000m, // 1 Billion BDT initial simulated treasury reserve
            SystemConstants.CurrencyBdt);

        typeof(Account).GetProperty(nameof(Account.Id))!.SetValue(treasuryAccount, SystemConstants.TreasuryAccountId);
        treasuryUser.SetAccount(treasuryAccount);

        await context.Users.AddAsync(treasuryUser);
        await context.Accounts.AddAsync(treasuryAccount);
        await context.SaveChangesAsync();

        logger?.LogInformation("Treasury account seeded with ID {Id}", SystemConstants.TreasuryAccountId);
    }

    /// <summary>
    /// Seeds demo users for development/testing ONLY.
    /// NEVER runs in production (enforced by isDevelopment gate in InitializeAsync).
    /// </summary>
    private static async Task SeedDemoUsersAsync(NCashDbContext context, IPasswordHasher passwordHasher, ILogger? logger)
    {
        // Only seed if treasury exists (prerequisite) and no demo users yet
        var treasuryAccount = await context.Accounts.FindAsync(SystemConstants.TreasuryAccountId);
        if (treasuryAccount == null) return;

        var demoUsers = new[]
        {
            ("Rahim Ahmed",    "rahim", "rahim@example.com", "+8801711111111", "Password123!", "ACC-100001"),
            ("Tasir Hossain",  "tasir", "tasir@example.com", "+8801722222222", "Password123!", "ACC-100002"),
            ("Saif Rahman",    "saif",  "saif@example.com",  "+8801733333333", "Password123!", "ACC-100003")
        };

        foreach (var (fullName, username, email, phone, password, accNum) in demoUsers)
        {
            if (await context.Users.AnyAsync(u => u.Username == username))
                continue;

            var user = new User(fullName, username, email, phone, passwordHasher.HashPassword(password), role: "User");
            var account = new Account(user.Id, accNum, 0m, SystemConstants.CurrencyBdt);
            user.SetAccount(account);

            await context.Users.AddAsync(user);
            await context.Accounts.AddAsync(account);
            await context.SaveChangesAsync();

            // Controlled System Issuance of BDT 100,000 from Treasury to User Account
            var initialAmount = SystemConstants.InitialUserBalance;
            var idempotencyKey = $"SEED-ISSUANCE-{user.Username}-{user.Id}";
            var txnNumber = $"TXN-SYS-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

            var txn = new Transaction(
                txnNumber,
                SystemConstants.TreasuryAccountId,
                account.Id,
                initialAmount,
                idempotencyKey,
                TransactionType.SystemIssuance,
                "Initial Controlled Simulated Funding (Development)",
                0m);

            txn.MarkProcessing();
            treasuryAccount.Debit(initialAmount);
            account.Credit(initialAmount);
            txn.MarkSucceeded();

            var debitEntry = new LedgerEntry(
                txn.Id,
                SystemConstants.TreasuryAccountId,
                LedgerDirection.Debit,
                initialAmount,
                treasuryAccount.Balance,
                $"System Issuance to {user.Username} ({accNum})");

            var creditEntry = new LedgerEntry(
                txn.Id,
                account.Id,
                LedgerDirection.Credit,
                initialAmount,
                account.Balance,
                "Controlled Initial Welcome Fund Issuance");

            var evtCreated = new TransactionEvent(txn.Id, "CREATED", "Seed transaction created.");
            var evtCompleted = new TransactionEvent(txn.Id, "COMPLETED", $"Issued {initialAmount} BDT to {accNum}.");

            await context.Transactions.AddAsync(txn);
            await context.LedgerEntries.AddRangeAsync(debitEntry, creditEntry);
            await context.TransactionEvents.AddRangeAsync(evtCreated, evtCompleted);
        }

        await context.SaveChangesAsync();
        logger?.LogInformation("Demo users seeded (Development only).");
    }

    /// <summary>
    /// Seeds the system auditor account for development/testing ONLY.
    /// NEVER runs in production. Production auditors must be provisioned via a secure admin flow.
    /// </summary>
    private static async Task SeedAuditorAsync(NCashDbContext context, IPasswordHasher passwordHasher, ILogger? logger)
    {
        if (await context.Users.AnyAsync(u => u.Username == "auditor"))
        {
            // Ensure auditor has an account with sufficient balance for TrustLab tests
            var existingAuditor = await context.Users.FirstAsync(u => u.Username == "auditor");
            var existingAuditorAccount = await context.Accounts.FirstOrDefaultAsync(a => a.UserId == existingAuditor.Id);
            if (existingAuditorAccount != null && existingAuditorAccount.Balance < 100000m)
            {
                existingAuditorAccount.Credit(100000m - existingAuditorAccount.Balance);
                await context.SaveChangesAsync();
            }
            return;
        }

        var auditorUser = new User(
            "System Auditor",
            "auditor",
            "auditor@ncash.internal",
            "+8801744444444",
            passwordHasher.HashPassword("AdminPass123!"),
            role: "Auditor");

        await context.Users.AddAsync(auditorUser);
        await context.SaveChangesAsync();

        var auditorAccount = new Account(auditorUser.Id, "ACC-AUDITOR", 100000m, SystemConstants.CurrencyBdt);
        auditorUser.SetAccount(auditorAccount);
        await context.Accounts.AddAsync(auditorAccount);
        await context.SaveChangesAsync();

        logger?.LogInformation("Auditor account seeded (Development only). Username: auditor");
    }
}
