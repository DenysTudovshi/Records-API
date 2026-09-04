using Whalebone.Records.Application.Abstractions;
using Whalebone.Records.Application.Domain;

namespace Whalebone.Records.Application.Records;

/// <summary>The record as the API exposes it. Property names are snake_cased on the wire.</summary>
public sealed record PersonRecordDto(
    Guid ExternalId,
    [property: PersonalData] string Name,
    [property: PersonalData] string Email,
    [property: PersonalData] DateTimeOffset DateOfBirth)
{
    public static PersonRecordDto From(PersonRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new PersonRecordDto(record.ExternalId, record.Name, record.Email, record.DateOfBirth);
    }
}
