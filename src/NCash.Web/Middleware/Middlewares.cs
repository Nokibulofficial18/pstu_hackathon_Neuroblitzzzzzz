using System.Net;
using System.Text.Json;
using NCash.Application.Common;
using NCash.Domain.Common;

namespace NCash.Web.Middleware;

public class GlobalExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionHandlerMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionHandlerMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (DomainException dex)
        {
            _logger.LogWarning("Domain exception occurred: {ErrorCode} - {Message} (Status: {StatusCode})",
                dex.ErrorCode, dex.Message, dex.StatusCode);

            await HandleExceptionAsync(context, dex.StatusCode, dex.ErrorCode, dex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled server exception during request {Path}: {Message}", context.Request.Path, ex.Message);
            var isDev = _env.IsDevelopment();
            var detail = isDev ? $"Internal error: {ex.Message}" : "An unexpected error occurred. Financial operations have been rolled back.";
            await HandleExceptionAsync(context, (int)HttpStatusCode.InternalServerError, "INTERNAL_SERVER_ERROR", detail);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, int statusCode, string errorCode, string message)
    {
        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = statusCode;

        var correlationId = context.Items["CorrelationId"]?.ToString() ?? context.TraceIdentifier;

        var problemDetails = new
        {
            type = $"https://api.ncash.local/errors/{errorCode.ToLowerInvariant().Replace('_', '-')}",
            title = GetTitleForStatusCode(statusCode),
            status = statusCode,
            detail = message,
            instance = context.Request.Path.Value,
            errorCode = errorCode,
            correlationId = correlationId,
            timestampUtc = DateTime.UtcNow,
            // Compatible with legacy client consumers
            isSuccess = false,
            message = message
        };

        var json = JsonSerializer.Serialize(problemDetails);
        await context.Response.WriteAsync(json);
    }

    private static string GetTitleForStatusCode(int statusCode) => statusCode switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        _ => "Internal Server Error"
    };
}

public class CorrelationIdMiddleware
{
    private const string CorrelationHeader = "X-Correlation-ID";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationHeader].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
        context.Response.Headers[CorrelationHeader] = correlationId;
        context.Items["CorrelationId"] = correlationId;

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TraceIdentifier"] = context.TraceIdentifier
        }))
        {
            await _next(context);
        }
    }
}
