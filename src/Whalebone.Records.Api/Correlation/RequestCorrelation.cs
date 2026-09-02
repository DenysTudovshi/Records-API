using Microsoft.Extensions.Primitives;

namespace Whalebone.Records.Api.Correlation;

/// <summary>
/// Gives every request an id, echoes it on every response - error responses included - and
/// holds it in the log scope for the life of the request.
/// </summary>
/// <remarks>
/// The header name matches the one Whalebone's own API returns, so a caller sitting in front
/// of both can correlate across them without a translation table.
/// </remarks>
internal static class RequestCorrelation
{
    internal const string HeaderName = "X-Request-Id";

    /// <summary>Long enough for a UUID or a Kestrel connection id, short enough to bound a log line.</summary>
    private const int MaxIdLength = 64;

    /// <summary>
    /// Not <c>RequestId</c>: the hosting scope already owns that key, and its value is captured
    /// before any user middleware runs, so reusing the name would put two different ids under
    /// one key in the same log line.
    /// </summary>
    private static readonly Func<ILogger, string, IDisposable?> CorrelationScope =
        LoggerMessage.DefineScope<string>("CorrelationId:{CorrelationId}");

    public static IApplicationBuilder UseRequestCorrelation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var logger = app.ApplicationServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RequestCorrelation));

        return app.Use(async (context, next) =>
        {
            var id = FromInboundHeader(context.Request.Headers[HeaderName]) ?? Guid.NewGuid().ToString();

            // TraceIdentifier rather than a parallel HttpContext.Items entry: the exception
            // handler and the ProblemDetails customisation both hold the context already, and
            // neither can read a response header that UseExceptionHandler has cleared.
            context.TraceIdentifier = id;

            // The only placement that survives the error path. UseExceptionHandler calls
            // Response.Clear() - which clears every header - before invoking IExceptionHandler,
            // so a header written here and now is gone by the time the 500 is written. The
            // OnStarting registration lives on the response feature, which Clear() leaves alone.
            context.Response.OnStarting(static state =>
            {
                var httpContext = (HttpContext)state;
                httpContext.Response.Headers[HeaderName] = httpContext.TraceIdentifier;
                return Task.CompletedTask;
            }, context);

            using (CorrelationScope(logger, id))
            {
                await next(context).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Honours an inbound id only if it could plausibly be one.
    /// </summary>
    /// <remarks>
    /// This value is echoed in a response header and stamped on every log line for the request,
    /// so reflecting arbitrary caller-supplied bytes is how a correlation id turns into a
    /// log-injection vector. An allow-list of the characters real request ids use - UUIDs, hex,
    /// Kestrel's <c>connection:counter</c> form - costs nothing and forecloses the whole class.
    /// Two headers are treated as no header: an ambiguous id is worse than a fresh one.
    /// </remarks>
    private static string? FromInboundHeader(StringValues inbound)
    {
        if (inbound.Count != 1)
        {
            return null;
        }

        var candidate = inbound[0];

        return string.IsNullOrEmpty(candidate)
               || candidate.Length > MaxIdLength
               || !candidate.All(IsIdCharacter)
            ? null
            : candidate;
    }

    private static bool IsIdCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.' or ':';
}
