using Augora.Core;
using Microsoft.AspNetCore.Diagnostics;

namespace Augora.Server;

/// <summary>
/// Turns <see cref="AugoraException"/> into RFC 9457 Problem Details with a <c>code</c> extension.
/// </summary>
internal sealed class ProblemDetailsExceptionHandler(IProblemDetailsService problemDetails, ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not AugoraException error)
        {
            return false;
        }

        logger.LogInformation("Request {Path} failed: {Code} {Message}", httpContext.Request.Path, error.Code, error.Message);
        httpContext.Response.StatusCode = error.StatusCode;
        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = error.StatusCode,
                Title = error.Message,
                Extensions = { ["code"] = error.Code },
            },
        };
        foreach (var (key, value) in error.Extensions)
        {
            context.ProblemDetails.Extensions[key] = value;
        }

        return await problemDetails.TryWriteAsync(context);
    }
}
