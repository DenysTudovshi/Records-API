using FluentValidation;

using Microsoft.AspNetCore.Diagnostics;

using Whalebone.Records.Api.Contracts;
using Whalebone.Records.Application.Abstractions;

namespace Whalebone.Records.Api.ExceptionHandling;

/// <summary>
/// Maps unhandled exceptions onto the <see cref="ErrorResponse"/> envelope: validation failures
/// become a <c>400</c> naming each parameter, and anything unexpected becomes a bare <c>500</c>
/// that never leaks exception text, type names or connection strings.
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

        // Read from TraceIdentifier and not the response header: UseExceptionHandler has already
        // called Response.Clear() by the time this runs, so the header is not there to read.
        var requestId = httpContext.TraceIdentifier;

        // object, not ErrorResponse: a 500 is the bare `error` schema rather than the envelope, and
        // the two are different shapes on purpose. WriteAsJsonAsync<object> serialises by runtime
        // type, so declaring it here is what stops the wrong one being written.
        (int Status, object Body) result = exception switch
        {
            ValidationException validation => (
                StatusCodes.Status400BadRequest,
                new ErrorResponse("Request validation failed", ToErrorDetails(validation), requestId)),

            // Kestrel-level read failures: an oversized body, a bad Content-Length, malformed
            // chunked encoding. Those messages describe the transport rather than our internals or
            // the caller's data, so relaying one is safe and genuinely useful. No parameter is
            // identifiable, so the envelope carries message alone.
            //
            // Note what does *not* arrive here: a malformed JSON body. Minimal API binding catches
            // its own JsonException without ever throwing, and the status-code handler in Program
            // writes that one. The two scalars a caller can plausibly misspell never reach either -
            // they are parsed leniently and rejected by the validator. See LenientGuidConverter.
            BadHttpRequestException badRequest => (
                badRequest.StatusCode,
                ErrorResponse.Plain(badRequest.Message, requestId)),

            _ => (StatusCodes.Status500InternalServerError, UnexpectedError(httpContext, exception, requestId)),
        };

        httpContext.Response.StatusCode = result.Status;

        await httpContext.Response
            .WriteAsJsonAsync<object>(result.Body, options: null, contentType: "application/json", cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path);

    /// <summary>
    /// One entry per failure, in the order the validators produced them.
    /// </summary>
    /// <remarks>
    /// No grouping by parameter: every rule chain stops at its first failure, so a parameter cannot
    /// appear twice. If that ever changes, a duplicate is better surfaced than silently merged.
    /// </remarks>
    private static ErrorDetail[] ToErrorDetails(ValidationException exception) =>
        exception.Errors
            .Select(failure => string.Equals(failure.ErrorCode, ValidationErrorCodes.Missing, StringComparison.Ordinal)
                ? ErrorDetail.Missing(failure.PropertyName, failure.ErrorMessage)
                : ErrorDetail.Invalid(failure.PropertyName, failure.ErrorMessage))
            .ToArray();

    private FaultResponse UnexpectedError(HttpContext httpContext, Exception exception, string requestId)
    {
        LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);

        // Their own 500 example, verbatim in shape: error, error_code, message, and nothing of ours.
        return FaultResponse.Unexpected("Unexpected error occurred.", requestId);
    }
}
