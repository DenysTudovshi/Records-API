using System.Net.Mail;

using FluentValidation;

using Whalebone.Records.Application.Domain;

namespace Whalebone.Records.Application.Records.Save;

/// <remarks>
/// Property names are overridden to their wire spelling so the 400 response keys its
/// <c>errors</c> object by the field the caller actually sent - <c>date_of_birth</c>,
/// not <c>DateOfBirth</c>.
/// </remarks>
public sealed class SaveRecordCommandValidator : AbstractValidator<SaveRecordCommand>
{
    public SaveRecordCommandValidator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        RuleFor(command => command.ExternalId)
            .NotEmpty().WithMessage("'external_id' is required and must be a non-empty UUID.")
            .OverridePropertyName("external_id");

        RuleFor(command => command.Name)
            .NotEmpty().WithMessage("'name' is required.")
            .MaximumLength(PersonRecord.NameMaxLength)
            .OverridePropertyName("name");

        RuleFor(command => command.Email)
            .NotEmpty().WithMessage("'email' is required.")
            .MaximumLength(PersonRecord.EmailMaxLength)
            // FluentValidation's EmailAddress() only looks for an '@' by design, and full
            // RFC 5322 in a regex is a known dead end. MailAddress plus a dotted host is
            // the pragmatic bar.
            .Must(BeAnAddressableEmail).WithMessage("'email' is not a valid email address.")
            .OverridePropertyName("email");

        RuleFor(command => command.DateOfBirth)
            .Must(value => value != default)
            .WithMessage("'date_of_birth' is required and must be an RFC 3339 timestamp.")
            .DependentRules(() =>
            {
                // No range check on the offset: DateTimeOffset cannot hold one outside +/-14:00,
                // so a rule here could never fail and its message could never be shown. An
                // out-of-range offset is refused a step earlier, when the value fails to parse.
                RuleFor(command => command.DateOfBirth)
                    .Must(value => value < timeProvider.GetUtcNow())
                    .WithMessage("'date_of_birth' must be in the past.")
                    .OverridePropertyName("date_of_birth");
            })
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
