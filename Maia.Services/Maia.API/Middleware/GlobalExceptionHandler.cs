using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Maia.API.Middleware;

/// <summary>
/// Centralised exception → ProblemDetails mapping (RFC 7807).
/// Keeps controllers clean — no try/catch needed for known exception types.
/// </summary>
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            ArgumentException or ArgumentNullException => (StatusCodes.Status400BadRequest,   "Bad Request"),
            DirectoryNotFoundException
                or KeyNotFoundException               => (StatusCodes.Status404NotFound,      "Not Found"),
            UnauthorizedAccessException              => (StatusCodes.Status403Forbidden,      "Forbidden"),
            _                                        => (StatusCodes.Status500InternalServerError, "Internal Server Error"),
        };

        logger.LogError(exception,
            "Unhandled {ExceptionType} → HTTP {StatusCode}: {Message}",
            exception.GetType().Name, status, exception.Message);

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title  = title,
            Detail = exception.Message,
        }, ct);

        return true;
    }
}
