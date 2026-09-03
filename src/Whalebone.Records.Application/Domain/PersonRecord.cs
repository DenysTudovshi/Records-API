namespace Whalebone.Records.Application.Domain;

/// <summary>
/// A stored person record.
/// </summary>
/// <remarks>
/// <para>
/// The date of birth is deliberately split into a UTC instant and the caller's UTC
/// offset, rather than held as a single <see cref="DateTimeOffset"/> column.
/// </para>
/// <para>
/// Npgsql maps <see cref="DateTimeOffset"/> onto <c>timestamptz</c> and rejects any
/// value whose offset is not zero, so the naive mapping throws on the primary happy
/// path. Normalising with <c>ToUniversalTime()</c> avoids the throw but answers
/// <c>+00:00</c> to a request that said <c>+02:00</c> - the same instant, a different
/// document. Storing the instant and the offset separately keeps the column sortable
/// and comparable while letting the API echo back exactly what the caller sent.
/// </para>
/// </remarks>
public sealed class PersonRecord
{
    /// <summary>
    /// Maximum stored length of <see cref="Name"/>.
    /// </summary>
    /// <remarks>
    /// Declared here because the validator and the column must agree. Held in two places they
    /// drift silently in the one direction that hurts: raise the validator alone and input it
    /// now accepts is input the column still refuses, turning a 400 into a 500.
    /// </remarks>
    public const int NameMaxLength = 200;

    /// <summary>RFC 3696 practical maximum for an email address. Shared for the same reason.</summary>
    public const int EmailMaxLength = 320;

    private PersonRecord()
    {
        // EF Core materialisation.
    }

    /// <summary>Surrogate key. Internal only - never appears on the wire.</summary>
    public long Id { get; private set; }

    /// <summary>Caller-supplied identity, and the only identifier a client can hold.</summary>
    public Guid ExternalId { get; private set; }

    public string Name { get; private set; } = null!;

    public string Email { get; private set; } = null!;

    /// <summary>The date of birth as an absolute instant, always <see cref="DateTimeKind.Utc"/>.</summary>
    public DateTime DateOfBirthUtc { get; private set; }

    /// <summary>The UTC offset the caller supplied, in minutes.</summary>
    public short DateOfBirthOffsetMinutes { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>The date of birth exactly as the caller supplied it, offset included.</summary>
    public DateTimeOffset DateOfBirth =>
        new DateTimeOffset(DateOfBirthUtc, TimeSpan.Zero)
            .ToOffset(TimeSpan.FromMinutes(DateOfBirthOffsetMinutes));

    public static PersonRecord Create(
        Guid externalId,
        string name,
        string email,
        DateTimeOffset dateOfBirth,
        DateTimeOffset now)
    {
        var record = new PersonRecord
        {
            ExternalId = externalId,
            CreatedAtUtc = now.UtcDateTime,
        };

        record.Apply(name, email, dateOfBirth, now);
        return record;
    }

    public void Update(string name, string email, DateTimeOffset dateOfBirth, DateTimeOffset now) =>
        Apply(name, email, dateOfBirth, now);

    private void Apply(string name, string email, DateTimeOffset dateOfBirth, DateTimeOffset now)
    {
        Name = name;
        Email = email;
        DateOfBirthUtc = dateOfBirth.UtcDateTime;
        DateOfBirthOffsetMinutes = (short)dateOfBirth.Offset.TotalMinutes;
        UpdatedAtUtc = now.UtcDateTime;
    }
}
