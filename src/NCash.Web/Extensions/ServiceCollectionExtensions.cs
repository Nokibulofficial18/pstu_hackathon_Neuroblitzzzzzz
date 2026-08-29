using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NCash.Application.Contracts.Persistence;
using NCash.Application.Contracts.Security;
using NCash.Application.Modules.Audit;
using NCash.Application.Modules.Auth;
using NCash.Application.Modules.GroupCollect;
using NCash.Application.Modules.Ledger;
using NCash.Application.Modules.MoneyRequests;
using NCash.Application.Modules.PaymentEngine;
using NCash.Application.Modules.RecoveryCenter;
using NCash.Application.Modules.RiskShield;
using NCash.Application.Modules.TrustLab;
using NCash.Application.Modules.Users;
using NCash.Application.Modules.Wallet;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Repositories;
using NCash.Infrastructure.Security;

namespace NCash.Web.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNCashInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        // ── P0 FIX: Production MUST never fall back to InMemory database ──────────────
        // Production fails closed if connection string is missing or is a placeholder.
        bool isTestOrDevelopment = environment.IsDevelopment()
            || environment.IsEnvironment("Test")
            || environment.IsEnvironment("Testing");

        bool connectionStringIsPlaceholder = string.IsNullOrWhiteSpace(connectionString)
            || connectionString.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("InMemory", StringComparison.OrdinalIgnoreCase);

        if (connectionStringIsPlaceholder && !isTestOrDevelopment)
        {
            throw new InvalidOperationException(
                "FATAL STARTUP ERROR: ConnectionStrings:DefaultConnection is missing or is a placeholder. " +
                "Production cannot start without a real PostgreSQL connection. " +
                "Set the environment variable ConnectionStrings__DefaultConnection with a valid PostgreSQL connection string.");
        }

        if (!connectionStringIsPlaceholder)
        {
            services.AddDbContext<NCashDbContext>(options =>
            {
                options.UseNpgsql(connectionString, npgsqlOptions =>
                {
                    npgsqlOptions.MigrationsAssembly(typeof(NCashDbContext).Assembly.FullName);
                    // Enable command timeout for safety
                    npgsqlOptions.CommandTimeout(60);
                });
                options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            });
        }
        else
        {
            // InMemory is ONLY acceptable for Development / Test when connection string is not set.
            // A warning is logged to ensure developers know this is a non-production configuration.
            services.AddDbContext<NCashDbContext>(options =>
                options.UseInMemoryDatabase("NCash_Dev_InMemory")
                       .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        }

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<NCashDbContext>());

        // Repositories
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<ITransactionRepository, TransactionRepository>();
        services.AddScoped<ILedgerRepository, LedgerRepository>();
        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();

        // Security
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();

        return services;
    }

    public static IServiceCollection AddNCashApplication(this IServiceCollection services)
    {
        // Core Isolated Payment Engine
        services.AddScoped<IPaymentEngine, PaymentEngine>();
        services.AddScoped<ITransferService, TransferService>();

        // Risk Engine
        services.AddScoped<IRiskShieldService, RiskShieldService>();

        // Business Modules
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IWalletService, WalletService>();
        services.AddScoped<IMoneyRequestService, MoneyRequestService>();
        services.AddScoped<ILedgerService, LedgerService>();
        services.AddScoped<IRecoveryCenterService, RecoveryCenterService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ITrustLabService, TrustLabService>();
        services.AddScoped<IGroupCollectService, GroupCollectService>();

        return services;
    }

    public static IServiceCollection AddNCashRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status429TooManyRequests;

            // Global default policy (100 req/min per IP)
            options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 100,
                        QueueLimit = 10,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // Auth rate limit (10 req/min per IP — stricter for login/register)
            options.AddPolicy("auth-limiter", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 10,
                        QueueLimit = 2,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // Financial transfers rate limit (30 req/min per authenticated user)
            options.AddPolicy("transfer-limiter", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                                  ?? httpContext.Connection.RemoteIpAddress?.ToString()
                                  ?? "anonymous",
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 30,
                        QueueLimit = 5,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // PIN verification rate limit (5 req/min per user — brute-force prevention)
            options.AddPolicy("pin-limiter", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                                  ?? httpContext.Connection.RemoteIpAddress?.ToString()
                                  ?? "anonymous",
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 5,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    }));
        });

        return services;
    }
}
