using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using NCash.Application.Contracts.Security;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Seed;
using NCash.Web.Extensions;
using NCash.Web.Middleware;

var builder = WebApplication.CreateBuilder(args);
var env = builder.Environment;
var configuration = builder.Configuration;

// ── 1. Fail-Closed: Validate critical configuration at startup ─────────────────────
// JWT secret and DB connection are validated inside AddNCashInfrastructure / AddNCashJwtAuthentication.
// Startup will throw with a clear message if secrets are missing in production.

// ── 2. Controllers and JSON serialization ─────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

// ── 3. NCash Modular Architecture Registrations ───────────────────────────────────
builder.Services.AddNCashInfrastructure(configuration, env);
builder.Services.AddNCashApplication();
builder.Services.AddNCashJwtAuthentication(configuration);
builder.Services.AddNCashSwagger();
builder.Services.AddNCashRateLimiting();

// ── 4. CORS Policy ───────────────────────────────────────────────────────────────
// P2 FIX: Production CORS is restricted to configured origins.
// Development allows localhost origins for convenience.
var allowedOrigins = configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("ProductionCors", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                  .AllowCredentials();
        }
        else if (env.IsDevelopment())
        {
            // Development: allow localhost on any port
            policy.SetIsOriginAllowed(origin =>
            {
                var uri = new Uri(origin);
                return uri.Host == "localhost" || uri.Host == "127.0.0.1";
            })
            .AllowAnyHeader()
            .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS");
        }
        else
        {
            // Production with no AllowedOrigins configured: deny all cross-origin requests.
            // Same-origin requests to the serving host are unaffected.
            policy.SetIsOriginAllowed(_ => false);
        }
    });
});

// ── 5. Health Checks (P5) ─────────────────────────────────────────────────────────
builder.Services.AddHealthChecks();

var app = builder.Build();

// ── 6. Database Migration & Controlled System Seeding ────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var context = services.GetRequiredService<NCashDbContext>();
        var passwordHasher = services.GetRequiredService<IPasswordHasher>();

        // P0 FIX: Pass isDevelopment to ensure demo data never seeds in production.
        await DbInitializer.InitializeAsync(context, passwordHasher, env.IsDevelopment(), logger);
        logger.LogInformation("NCash database initialized successfully. Environment: {Env}", env.EnvironmentName);
    }
    catch (Exception ex)
    {
        var logger2 = services.GetRequiredService<ILogger<Program>>();
        logger2.LogCritical(ex, "FATAL: Database initialization failed. Application will not start.");
        // Rethrow to prevent the app from serving traffic with an uninitialized database.
        throw;
    }
}

// ── 7. Middleware Pipeline ────────────────────────────────────────────────────────
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionHandlerMiddleware>();

// P3 FIX: Security Headers
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";

    // Content-Security-Policy — narrow policy since frontend and API share the same origin
    // 'unsafe-inline' is needed for current inline JS in index.html; should be refactored away
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://fonts.gstatic.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none';";

    await next();
});

// P3 FIX: HSTS in production
if (!env.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors("ProductionCors");

// P2 FIX: Swagger only in Development
if (env.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "NCash API v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Health check endpoints (P5)
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,  // Liveness: just check if the process is running
    ResponseWriter = async (context, _) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"status\":\"live\"}");
    }
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var status = report.Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy
            ? "ready" : "not-ready";
        await context.Response.WriteAsync($"{{\"status\":\"{status}\"}}");
    }
});

// Fallback to SPA index.html
app.MapFallbackToFile("index.html");

app.Run();

// For integration testing support
public partial class Program { }
