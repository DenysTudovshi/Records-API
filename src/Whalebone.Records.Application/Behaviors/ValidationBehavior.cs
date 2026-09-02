using FluentValidation;

// CA2016 fires on next(), but MediatR 12's RequestHandlerDelegate<TResponse> is
// parameterless - there is no overload to forward the token to. Cancellation still
// flows: it is passed to every validator below and on to the handler by MediatR.
#pragma warning disable CA2016

using MediatR;

namespace Whalebone.Records.Application.Behaviors;

/// <summary>
/// Runs every registered validator for a request before its handler. Failures surface
/// as a <see cref="ValidationException"/>, which the API's exception handler renders as
/// RFC 7807 with per-field errors.
/// </summary>
/// <remarks>
/// This is the only pipeline behaviour in the service, and the reason MediatR earns its
/// place: validation is declared once, beside the command, and applies to every use case
/// without per-endpoint wiring.
/// </remarks>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        var applicable = validators as IValidator<TRequest>[] ?? validators.ToArray();
        if (applicable.Length == 0)
        {
            return await next().ConfigureAwait(false);
        }

        var context = new ValidationContext<TRequest>(request);
        var results = await Task.WhenAll(
                applicable.Select(validator => validator.ValidateAsync(context, cancellationToken)))
            .ConfigureAwait(false);

        var failures = results.SelectMany(result => result.Errors).ToArray();

        return failures.Length > 0
            ? throw new ValidationException(failures)
            : await next().ConfigureAwait(false);
    }
}
#pragma warning restore CA2016
