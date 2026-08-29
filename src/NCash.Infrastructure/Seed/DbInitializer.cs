using Microsoft.EntityFrameworkCore;
using NCash.Application.Contracts.Security;
using NCash.Domain.Common;
using NCash.Domain.Entities;
using NCash.Domain.Enums;
using NCash.Infrastructure.Persistence;

namespace NCash.Infrastructure.Seed;

public static class DbInitializer
{
    public static async Task InitializeAsync(NCashDbContext context, IPasswordHasher passwordHasher)
    {
        if (context.Database.IsRelational())
        {
            try
            {
                await context.Database.MigrateAsync();
            }
            catch
            {
                await context.Database.EnsureCreatedAsync();
            }
        }
        else
        {
            await context.Database.EnsureCreatedAsync();
        }

        // 1. Seed Treasury User and Account (if not present)
        if (!await context.Accounts.AnyAsync(a => a.Id == SystemConstants.TreasuryAccountId))
        {
            var treasuryUser = new User(
                "N-Cash System Treasury",
                "system.treasury",
                "treasury@ncash.internal",
                "+8801700000000",
                passwordHasher.HashPassword(Guid.NewGuid().ToString("N")),
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

            // 2. Seed Initial Demo Users
            var demoUsers = new[]
            {
                ("Rahim Ahmed", "rahim", "rahim@example.com", "+8801711111111", "Password123!", "ACC-100001"),
                ("Tasir Hossain", "tasir", "tasir@example.com", "+8801722222222", "Password123!", "ACC-100002"),
                ("Saif Rahman", "saif", "saif@example.com", "+8801733333333", "Password123!", "ACC-100003")
            };

            foreach (var (fullName, username, email, phone, password, accNum) in demoUsers)
            {
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
                    "Initial Controlled Simulated Funding",
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
        }

        // 3. Ensure System Auditor User AND Account exist
        var auditorUser = await context.Users.FirstOrDefaultAsync(u => u.Username == "auditor");
        if (auditorUser == null)
        {
            auditorUser = new User(
                "System Auditor",
                "auditor",
                "auditor@ncash.internal",
                "+8801744444444",
                passwordHasher.HashPassword("AdminPass123!"),
                role: "Auditor");

            await context.Users.AddAsync(auditorUser);
            await context.SaveChangesAsync();
        }

        var auditorAccount = await context.Accounts.FirstOrDefaultAsync(a => a.UserId == auditorUser.Id);
        if (auditorAccount == null)
        {
            auditorAccount = new Account(auditorUser.Id, "ACC-AUDITOR", 100000m, SystemConstants.CurrencyBdt);
            auditorUser.SetAccount(auditorAccount);
            await context.Accounts.AddAsync(auditorAccount);
            await context.SaveChangesAsync();
        }
        else if (auditorAccount.Balance < 100000m)
        {
            auditorAccount.Credit(100000m - auditorAccount.Balance);
            await context.SaveChangesAsync();
        }
    }
}
