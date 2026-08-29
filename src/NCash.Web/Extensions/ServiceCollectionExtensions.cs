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
    public static IServiceCollection AddNCashInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (!string.IsNullOrEmpty(connectionString) && !connectionString.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContext<NCashDbContext>(options =>
            {
                options.UseNpgsql(connectionString, npgsqlOptions =>
                {
                    npgsqlOptions.MigrationsAssembly(typeof(NCashDbContext).Assembly.FullName);
                    npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
                });
                options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            });
        }
        else
        {
            // In-Memory Database fallback for fast offline development or unit testing
            services.AddDbContext<NCashDbContext>(options =>
                options.UseInMemoryDatabase("NCash_ClosedSimulated_Db")
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

            // Global default policy (100 req/min)
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

            // Auth rate limit (20 req/min)
            options.AddPolicy("auth-limiter", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 20,
                        QueueLimit = 5,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // Financial transfers rate limit (30 req/min)
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
        });

        return services;
    }
}
