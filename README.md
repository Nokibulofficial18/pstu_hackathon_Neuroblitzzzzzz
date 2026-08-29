# N-Cash - Failure-Safe Money Movement Platform

[![.NET](https://img.shields.io/badge/.NET-8.0%20%2F%2010.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-ACID%20Ledger-336791?logo=postgresql)](https://www.postgresql.org/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

> **"We are not optimizing only for successful transfers. We are optimizing for correct transfers under failure."**

**N-Cash** is a production-grade, failure-safe money movement platform designed for closed simulated financial ecosystems. It addresses the core engineering challenge of real-world money movement: **guaranteeing atomicity, idempotency, concurrency safety, balance protection, auditability, and mathematical consistency under retries, collisions, and network failures.**

---

## 1. System Architecture (Modular Monolith)

N-Cash is structured as a clean, decoupled **Modular Monolith** using **ASP.NET Core 8 Web API** and **Entity Framework Core**:

```
NCash/
├── src/
│   ├── TrustFlow.Domain/                # Pure domain entities, Value objects, Enums, Exceptions
│   │   ├── Common/                      # BaseEntity, DomainException, Result, SystemConstants
│   │   ├── Entities/                    # User, Account, Transaction, LedgerEntry, TransactionEvent,
│   │   │                                # MoneyRequest, RiskSignal, DisputeCase, IdempotencyRecord
│   │   └── Enums/                       # TransactionStatus, LedgerDirection, RiskLevel, DisputeStatus
│   │
│   ├── TrustFlow.Infrastructure/        # EF Core DbContext, PostgreSQL configs, Pessimistic Row Locking
│   │   ├── Persistence/                 # TrustFlowDbContext, EntityConfigurations
│   │   ├── Repositories/                # AccountRepository (SELECT FOR UPDATE), TransactionRepository
│   │   ├── Security/                    # BCrypt PasswordHasher, JwtTokenGenerator
│   │   └── Seed/                        # Controlled N-Cash System Treasury Issuance & Demo accounts
│   │
│   ├── TrustFlow.Application/           # Business orchestration, isolated engines, DTOs, contracts
│   │   ├── Contracts/                   # IApplicationDbContext, IRepositories, ISecurityContracts
│   │   └── Modules/
│   │       ├── PaymentEngine/           # IPaymentEngine (ISOLATED ENGINE), ITransferService
│   │       ├── RiskShield/              # IRiskShieldService (Explainable deterministic rules)
│   │       ├── Auth/                    # IAuthService (Register + Auto BDT 100k Issuance, Login, PIN)
│   │       ├── Users/                   # IUserService (Receiver lookup, profile verification)
│   │       ├── Wallet/                  # IWalletService (Balance & transaction summaries)
│   │       ├── MoneyRequests/           # IMoneyRequestService (Full & Partial payments)
│   │       ├── Ledger/                  # ILedgerService (Double-Entry audit & Global Reconciliation)
│   │       ├── RecoveryCenter/          # IRecoveryCenterService (Inquiries & dispute tracking)
│   │       ├── Audit/                   # IAuditService (Security & chronological timeline trace)
│   │       └── TrustLab/                # ITrustLabService (Chaos simulator for live judge demos)
│   │
│   └── TrustFlow.Web/                   # ASP.NET Core Web API Host & Minimal Client Interface
│       ├── Controllers/                 # REST API Controllers
│       ├── Middleware/                  # GlobalExceptionMiddleware, CorrelationIdMiddleware
│       ├── Extensions/                  # Dependency injection, Swagger, JWT Bearer, Rate Limiting
│       └── wwwroot/                     # Minimalist live N-Cash test client
│
└── tests/
    └── TrustFlow.Tests/                 # Comprehensive xUnit automated test suite
        ├── PaymentEngineTests.cs        # Atomicity, Overdraft prevention, Idempotency
        ├── AuthAndSecurityTests.cs      # User Registration, JWT, BCrypt, PIN, Anti-Enumeration
        ├── GroupCollectionTests.cs      # Group Collect Pools & Contribution tracking
        └── RiskShieldAndLedgerTests.cs  # Risk rules & Global Double-Entry Invariant
```

---

## 2. Core Financial Correctness Guarantees

### A. Atomicity & Deadlock-Free Row Locking
- Debit, credit, immutable ledger entries, and timeline events are executed inside **ONE PostgreSQL ACID database transaction**.
- To prevent deadlocks when User A sends to User B while User B sends to User A simultaneously:
  1. IDs are sorted deterministically: `var (firstId, secondId) = Sort(senderId, receiverId)`.
  2. Rows are locked in consistent order:
     ```sql
     SELECT * FROM accounts WHERE "Id" = ANY(@orderedIds) ORDER BY "Id" FOR UPDATE;
     ```
  3. Balances are validated strictly before mutation. If anything fails, PostgreSQL completely rolls back the entire batch.

### B. Duplicate Payment Shield (Idempotency)
- Every transfer request requires an `Idempotency-Key` HTTP header (UUID).
- Repeated submissions with the same key return the original transaction receipt with `isCached: true` without performing a second debit.

### C. Immutable Double-Entry Ledger & Conservation of Money
- Every user-to-user transfer generates two paired entries:
  - Sender: `Direction = DEBIT`, `Amount = X`, `BalanceAfter = Balance - X`
  - Receiver: `Direction = CREDIT`, `Amount = X`, `BalanceAfter = Balance + X`
- **Mathematical Invariant**: $\sum \text{Debits} = \sum \text{Credits} \implies \text{Net Variance} = 0.00$.
- **System Conservation Invariant**: $\text{Total Treasury Issued} = \text{Total Circulating Balances}$.

### D. Deterministic Risk Shield
- Rule-based transparent risk evaluation (no opaque AI claims):
  - **New Recipient** (+30 pts): First transaction between the two users.
  - **Large Amount** (+25 pts): Transfer $\ge$ BDT 25,000.
  - **High Velocity** (+20 pts): $\ge$ 3 transfers in the last 2 minutes.
  - **Off-Peak Hours** (+10 pts): Transfers between 20:00 and 04:00 UTC.
- High risk triggers mandatory step-up confirmation on the client.

---

## 3. Seeded Accounts & Simulated Funds

When N-Cash boots, `DbInitializer` seeds:
1. **N-Cash System Treasury Account** (`ACC-SYSTEM-TREASURY`): 1 Billion BDT initial reserve.
2. **Demo Users** (each automatically funded with BDT 100,000 via a controlled `SystemIssuance` ledger event):
   - **Rahim Ahmed**: `rahim` / `Password123!` (Account: `ACC-100001`)
   - **Tasir Hossain**: `tasir` / `Password123!` (Account: `ACC-100002`)
   - **Saif Rahman**: `saif` / `Password123!` (Account: `ACC-100003`)
   - **System Auditor**: `auditor` / `AdminPass123!` (Role: `Auditor`)
3. **New User Registrations**: Automatically receive BDT 100,000 from System Treasury with full ledger issuance records.

---

## 4. Running Locally

### Prerequisites
- .NET 8.0 or 10.0 SDK
- PostgreSQL 14+ (Optional: automatically falls back to In-Memory DB if PostgreSQL is not running)

### Configuration
Update `src/TrustFlow.Web/appsettings.json` or set environment variables:
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=ncash_db;Username=postgres;Password=postgres"
  },
  "Jwt": {
    "Secret": "NCash_Super_Secure_Secret_Key_For_Hackathon_2026_Min_32_Chars!",
    "Issuer": "NCash",
    "Audience": "NCashUsers",
    "ExpiryMinutes": 1440
  }
}
```

### Running the API & Client
```powershell
dotnet run --project src/TrustFlow.Web/TrustFlow.Web.csproj
```

Open your browser:
- **N-Cash Web Client**: `http://localhost:5000` (or `https://localhost:5001`)
- **Swagger OpenAPI Docs**: `http://localhost:5000/swagger`

### Running Automated xUnit Tests
```powershell
dotnet test tests/TrustFlow.Tests/TrustFlow.Tests.csproj
```

---

## 5. Live Demo Script for Judges (N-Cash Trust Lab)

1. **Normal Transfer**: Log in as Rahim (`ACC-100001`), send BDT 2,500 to Tasir (`ACC-100002`). Open Transaction Details to view the chronological execution timeline (`ACCOUNTS_LOCKED` $\to$ `DEBIT` $\to$ `CREDIT` $\to$ `COMMITTED`).
2. **Duplicate Attack Test**: In the **Trust Lab** tab, click **"Run Duplicate Attack Simulation"**. Notice 5 rapid requests fire with the same Idempotency-Key; exactly 1 transfer commits, and 4 return cached receipts with 0 duplicate deduction.
3. **Concurrency Race Test**: Click **"Run Concurrency Race Simulation"**. 2 parallel transfers of BDT 70,000 fire against the wallet balance simultaneously. Notice 1 succeeds, 1 is rejected gracefully due to insufficient balance, and final balance is strictly $\ge 0$ (Zero Overdraft).
4. **Network Timeout & Auto-Recovery**: Click **"Run Timeout & Recovery Simulation"** to observe state progression: `CREATED` $\to$ `PROCESSING` $\to$ `UNKNOWN / RECOVERING` $\to$ `SUCCEEDED`.
5. **Mathematical Ledger Reconciliation**: Click **"Run Global Audit Check"** to show judges live that $\sum \text{Debits} = \sum \text{Credits}$ with 0.00 variance and 100% funds conservation.
