using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

namespace NCash.Web.Extensions;

public static class JwtExtensions
{
    public static IServiceCollection AddNCashJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwtSecret = configuration["Jwt:Secret"] ?? "NCash_Super_Secure_Secret_Key_For_Hackathon_2026_Min_32_Chars!";
        var issuer = configuration["Jwt:Issuer"] ?? "NCash";
        var audience = configuration["Jwt:Audience"] ?? "NCashUsers";

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = false;
            options.SaveToken = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
        });

        // Role-based Authorization Policies
        services.AddAuthorization(options =>
        {
            options.AddPolicy("RequireAdmin", policy => policy.RequireRole("Admin"));
            options.AddPolicy("RequireAuditor", policy => policy.RequireRole("Auditor", "Admin"));
            options.AddPolicy("RequireUser", policy => policy.RequireRole("User", "Admin"));
        });

        return services;
    }
}

public static class SwaggerExtensions
{
    public static IServiceCollection AddNCashSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "N-Cash API",
                Version = "v1",
                Description = "N-Cash Failure-Safe Digital Money Movement Platform. Guaranteed atomic, idempotent, and concurrency-safe transactions with double-entry ledger."
            });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "Enter JWT Bearer token format: Bearer {your token}",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer"
            });
        });

        return services;
    }
}
