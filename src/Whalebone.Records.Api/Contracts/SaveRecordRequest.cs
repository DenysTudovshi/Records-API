using Whalebone.Records.Application.Records.Save;

namespace Whalebone.Records.Api.Contracts;

/// <summary>
/// The <c>POST /save</c> request body.
/// </summary>
/// <remarks>
/// Every member is nullable on purpose. A missing field then arrives as null and is
/// rejected by the validator with a precise, field-keyed 400, instead of binding to a
/// default that silently looks valid. A malformed uuid or timestamp arrives the same way,
/// via <see cref="LenientGuidConverter"/> - so "absent" and "unreadable" get the same
/// treatment, rather than the second one falling out of the binder as a generic 400 that
/// names no field at all.
/// </remarks>
public sealed record SaveRecordRequest(
    Guid? ExternalId,
    string? Name,
    string? Email,
    DateTimeOffset? DateOfBirth)
{
    /// <remarks>
    /// Straight through, deliberately. Substituting defaults here would erase the one distinction
    /// the error contract reports: a field the caller omitted against one they sent in a form this
    /// service cannot use.
    /// </remarks>
    internal SaveRecordCommand ToCommand() => new(ExternalId, Name, Email, DateOfBirth);
}
