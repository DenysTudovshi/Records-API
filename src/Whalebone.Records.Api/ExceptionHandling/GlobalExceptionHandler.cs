using FluentValidation;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Whalebone.Records.Api.ExceptionHandling;

/// <summary>
/// Maps unhandled exceptions onto RFC 7807 responses: validation failures become a 400
/// carrying per-field errors, and anything unexpected becomes a bare 500 that never
/// leaks exception text, type names or connection strings.
/// </summary>
internal sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        ProblemDetails problem = exception switch
        {
            ValidationException validation => new ValidationProblemDetails(ToFieldErrors(validation))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation failed.",
            },

            // Kestrel-level read failures: an oversized body, a bad Content-Length, malformed
            // chunked encoding. Those messages describe the transport rather than our internals
            // or the caller's data, so relaying one is safe and genuinely useful.
            //
            // Note what does *not* arrive here: a malformed JSON body. Minimal API binding
            // catches its own JsonException without ever throwing, and answers with the generic
            // problem body - correct status, no errors member, no field named. That is why the
            // two scalars a caller can plausibly misspell are parsed leniently and rejected by
            // the validator instead - see LenientGuidConverter.
            BadHttpRequestException badRequest => new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Title = "The request could not be read.",
                Detail = badRequest.Message,
            },

            _ => UnexpectedError(httpContext, exception),
        };

        // CustomizeProblemDetails does not reach this body: writing it directly is exactly
        // what bypasses IProblemDetailsService, so the handler stamps the id itself. The key
        // is spelled snake_case at the source - extension members are written verbatim, the
        // naming policy never sees them, and a test pins that.
        problem.Extensions["request_id"] = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = problem.Status!.Value;

        // The explicit <object> matters: serialising as ProblemDetails would slice off
        // ValidationProblemDetails and silently drop the "errors" member.
        await httpContext.Response
            .WriteAsJsonAsync<object>(problem, options: null, contentType: "application/problem+json", cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path);

    private static Dictionary<string, string[]> ToFieldErrors(ValidationException exception) =>
        exception.Errors
            .GroupBy(failure => failure.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

    private ProblemDetails UnexpectedError(HttpContext httpContext, Exception exception)
    {
        LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);

        return new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
        };
    }
}
