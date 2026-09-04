using System.Net.Mail;

using FluentValidation;

using Whalebone.Records.Application.Abstractions;
using Whalebone.Records.Application.Domain;

namespace Whalebone.Records.Application.Records.Save;

/// <remarks>
/// <para>
/// Property names are overridden to their wire spelling, so an error names the field the caller
/// actually sent - <c>date_of_birth</c>, not <c>DateOfBirth</c>.
/// </para>
/// <para>
/// Every rule carries a <see cref="ValidationErrorCodes"/> code, and every chain stops at its first
/// failure. Both serve the same end: the caller is told once that a field was absent, or once that
/// it was unusable, rather than handed a pile of consequences of the same mistake.
/// </para>
/// </remarks>
public sealed class SaveRecordCommandValidator : AbstractValidator<SaveRecordCommand>
{
    public SaveRecordCommandValidator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        RuleFor(command => command.ExternalId)
            .Cascade(CascadeMode.Stop)
            .NotNull()
                .WithErrorCode(ValidationErrorCodes.Missing)
                .WithMessage("'external_id' is required.")
            .Must(value => value != Guid.Empty)
                .WithErrorCode(ValidationErrorCodes.Invalid)
                .WithMessage("'external_id' must be a non-empty UUID.")
            .OverridePropertyName("external_id");

        RuleFor(command => command.Name)
            .Cascade(CascadeMode.Stop)
            .NotNull()
                .WithErrorCode(ValidationErrorCodes.Missing)
                .WithMessage("'name' is required.")
            .NotEmpty()
                .WithErrorCode(ValidationErrorCodes.Invalid)
                .WithMessage("'name' must not be blank.")
            .MaximumLength(PersonRecord.NameMaxLength)
                .WithErrorCode(ValidationErrorCodes.Invalid)
                .WithMessage($"'name' must be {PersonRecord.NameMaxLength} characters or fewer.")
            .OverridePropertyName("name");

        RuleFor(command => command.Email)
            .Cascade(CascadeMode.Stop)
            .NotNull()
                .WithErrorCode(ValidationErrorCodes.Missing)
                .WithMessage("'email' is required.")
            .NotEmpty()
                .WithErrorCode(ValidationErrorCodes.Invalid)
                .WithMessage("'email' must not be blank.")
            .MaximumLength(PersonRecord.EmailMaxLength)
                .WithErrorCode(ValidationErrorCodes.Invalid)
                .WithMessage($"'email' must be {PersonRecord.EmailMaxLength} characters or fewer.")
            // FluentValidation's EmailAddress() only looks for an '@' by design, and full
            // RFC 5322 in a regex is a known dead end. MailAddress plus a dotted host is
            // the pragmatic bar.
            .Must(BeAnAddressableEmail)
                .WithErrorCode(ValidationErrorCodes.Invalid)
                .WithMessage("'email' is not a valid email address.")
            .OverridePropertyName("email");

        RuleFor(command => command.DateOfBirth)
            .Cascade(CascadeMode.Stop)
            .NotNull()
                .WithErrorCode(ValidationErrorCodes.Missing)
                .WithMessage("'date_of_birth' is required.")
            // The converter yields the default for anything it could not read, offset-less
            // timestamps included, so this is where an unreadable value surfaces.
            .Must(value => value != default(DateTimeOffset))
                .WithErrorCode(ValidationErrorCodes.Invalid)
                .WithMessage("'date_of_birth' must be an RFC 3339 timestamp carrying a UTC offset.")
            .Must(value => value < timeProvider.GetUtcNow())
                .WithErrorCode(ValidationErrorCodes.Invalid)
                .WithMessage("'date_of_birth' must be in the past.")
            .OverridePropertyName("date_of_birth");
    }

    private static bool BeAnAddressableEmail(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > PersonRecord.EmailMaxLength)
        {
            return false;
        }

        if (!MailAddress.TryCreate(candidate, out var address))
        {
            return false;
        }

        // MailAddress accepts "user@localhost"; a public API wants a dotted, non-terminal host.
        var host = address.Host;
        return host.Contains('.', StringComparison.Ordinal)
            && !host.StartsWith('.')
            && !host.EndsWith('.')
            && address.Address.Equals(candidate.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
