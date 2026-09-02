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
    /// <summary>RFC 3696 practical maximum for an email address.</summary>
    private const int EmailMaxLength = 320;

    private const int NameMaxLength = 200;

    public SaveRecordCommandValidator(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        RuleFor(command => command.ExternalId)
            .NotEmpty().WithMessage("'external_id' is required and must be a non-empty UUID.")
            .OverridePropertyName("external_id");

        RuleFor(command => command.Name)
            .NotEmpty().WithMessage("'name' is required.")
            .MaximumLength(NameMaxLength)
            .OverridePropertyName("name");

        RuleFor(command => command.Email)
            .NotEmpty().WithMessage("'email' is required.")
            .MaximumLength(EmailMaxLength)
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
                RuleFor(command => command.DateOfBirth)
                    .Must(value => value < timeProvider.GetUtcNow())
                    .WithMessage("'date_of_birth' must be in the past.")
                    .Must(value => Math.Abs(value.Offset.TotalMinutes) <= PersonRecord.MaxOffsetMinutes)
                    .WithMessage("'date_of_birth' must carry a UTC offset within +/-14:00.")
                    .OverridePropertyName("date_of_birth");
            })
            .OverridePropertyName("date_of_birth");
    }

    private static bool BeAnAddressableEmail(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > EmailMaxLength)
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
