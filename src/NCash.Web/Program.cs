using System.Text.Json.Serialization;
using NCash.Application.Contracts.Security;
using NCash.Infrastructure.Persistence;
using NCash.Infrastructure.Seed;
using NCash.Web.Extensions;
using NCash.Web.Middleware;

var builder = WebApplication.CreateBuilder(args);

// 1. Controllers and JSON serialization
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

// 2. NCash Modular Architecture Registrations
builder.Services.AddNCashInfrastructure(builder.Configuration);
builder.Services.AddNCashApplication();
builder.Services.AddNCashJwtAuthentication(builder.Configuration);
builder.Services.AddNCashSwagger();
builder.Services.AddNCashRateLimiting();

// 3. CORS Policy
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// 4. Database Migration & Controlled System Seeding
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var context = services.GetRequiredService<NCashDbContext>();
        var passwordHasher = services.GetRequiredService<IPasswordHasher>();
        await DbInitializer.InitializeAsync(context, passwordHasher);
        logger.LogInformation("NCash Database initialized and seeded successfully.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred during database migration/initialization.");
    }
}

// 5. Middleware Pipeline
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionHandlerMiddleware>();

app.UseCors("AllowAll");

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "NCash API v1");
    c.RoutePrefix = "swagger";
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Fallback to minimal index.html for SPA client
app.MapFallbackToFile("index.html");

app.Run();

// For integration testing support
public partial class Program { }
