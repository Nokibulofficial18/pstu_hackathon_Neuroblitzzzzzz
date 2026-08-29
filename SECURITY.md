# Security Policy & Incident Remediation Guide — N-Cash

## 1. Overview & Threat Model
NCash is a failure-safe financial ledger and digital money movement engine. Financial applications require zero tolerance for secret exposure, double-spending, race conditions, parameter tampering, and unauthorized state transitions.

---

## 2. Secrets Management & Rotation Checklist (Post-Incident)

> [!CAUTION]
> If repository secrets (PostgreSQL database credentials or JWT signing secrets) were previously committed to Git history, treat them as compromised immediately.

Follow this checklist for immediate manual remediation:

1. **Rotate Database Credentials**:
   - Log into Supabase / AWS / Managed PostgreSQL dashboard.
   - Immediately regenerate the database user password.
   - Terminate all active database pool connections.
2. **Rotate JWT Signing Key**:
   - Generate a new, cryptographically secure 256-bit+ random key (min 32 characters).
   - Set the new key in production environment variables (`Jwt__Secret`).
   - Existing user sessions will be invalidated immediately.
3. **Audit Database Logs**:
   - Review query logs for unauthorized external IP connections or suspicious queries during the exposure window.
4. **Git History Scrubbing**:
   - Use `git-filter-repo` or BFG Repo-Cleaner to permanently excise historical commit objects containing credentials.
   ```bash
   # Example with git-filter-repo
   git filter-repo --replace-text <(echo 'OLD_PASSWORD==>REDACTED')
   git push origin --force --all
   ```
5. **Set Environment Variables**:
   - Production systems MUST configure secrets strictly via Environment Variables or Key Vault (e.g. AWS Secrets Manager / Azure Key Vault).
   - Never commit `appsettings.Production.json` or `.env` files.

---

## 3. Financial Invariants & Security Architecture

NCash enforces the following non-negotiable security controls:

### A. Idempotency & Replay Protection
- Every financial mutation (Transfers, Money Request payments, Group Collections) requires a client-supplied unique `Idempotency-Key` HTTP Header.
- In-flight duplicate requests return HTTP `409 Conflict`.
- Completed duplicate requests return the cached response without re-executing balance mutations.

### B. Double-Entry Invariant & Zero-Variance
- Every transaction creates exactly balanced double-entry `LedgerEntry` records (`Debit == Credit`).
- Balance deduction from sender + fee allocation + credit to recipient must sum to zero delta. Any deviation triggers an immediate transaction rollback.

### C. Concurrency & Overdraft Defense
- Relational account balances are locked via deterministic row-level locking (`SELECT ... FOR UPDATE`) ordered by account ID to prevent deadlocks and race conditions.
- Balances are strictly validated under lock prior to state mutation.

### D. Composite Transaction Atomicity
- Complex multi-step operations (e.g. paying a Money Request or contributing to a Group Collection) execute inside a single atomic database transaction (`IDbContextTransaction`). If either the ledger transfer or the domain entity state update fails, the entire transaction is rolled back.

### E. Rate Limiting & Brute-Force Defenses
- Authentication endpoints (`/api/auth/login`, `/api/auth/register`): rate limited to 10 req/min per IP.
- PIN verification (`/api/auth/pin/verify`): rate limited to 5 req/min per user with automatic account-level lockouts triggered after 5 consecutive failures within a 15-minute window.
- Financial transfers (`/api/transfers`): rate limited to 30 req/min per user.

### F. Least Privilege & Authorization (IDOR Prevention)
- Direct Object Reference checks ensure users can only access their own transactions, wallets, and disputes.
- Privileged operations (investigations, global audits, TrustLab diagnostics) require `Admin` or `Auditor` roles.

---

## 4. Reporting a Security Vulnerability
If you discover a potential vulnerability in this codebase, please contact the security team or open a private security advisory on GitHub rather than filing a public issue.
