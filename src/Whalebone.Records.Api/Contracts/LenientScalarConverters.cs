using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whalebone.Records.Api.Contracts;

/// <summary>
/// Shared behaviour for the two converters below.
/// </summary>
internal static class LenientScalar
{
    /// <summary>
    /// Steps past a value a converter is refusing, so deserialisation continues to the next member
    /// instead of stalling on the token it could not read.
    /// </summary>
    /// <remarks>
    /// Scalars leave the reader on their single complete token, so only a composite needs skipping.
    /// <c>Skip</c> rather than <c>TrySkip</c> is safe here: before invoking a custom converter,
    /// <see cref="JsonSerializer"/> reads ahead until the whole value is buffered, so
    /// <c>isFinalBlock</c> is never false inside <c>Read</c>. Verified against a 40 KB object
    /// delivered one byte at a time through <c>DeserializeAsync</c>.
    /// </remarks>
    internal static void Consume(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    /// <summary>
    /// True when a timestamp carries an explicit UTC offset, as RFC 3339 requires.
    /// </summary>
    /// <remarks>
    /// <c>TryGetDateTimeOffset</c> implements the wider ISO 8601 profile, where the offset is
    /// optional, and resolves an offset-less value against the <em>host's</em> local time zone. So
    /// <c>"2020-01-01"</c> becomes <c>+01:00</c> on a CET machine, <c>+02:00</c> in July, and
    /// <c>+00:00</c> in this service's own UTC container: the same request, stored three different
    /// ways depending on where it landed. This service promises to echo back exactly what the
    /// caller sent, which it cannot do for an offset the caller never sent.
    /// </remarks>
    internal static bool CarriesAnOffset(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // RFC 3339 accepts either case. Only 'Z' can reach here today - TryGetDateTimeOffset runs
        // first and refuses the lowercase spelling - so 'z' is belt-and-braces against that
        // upstream strictness relaxing, not a case this service currently sees.
        if (value[^1] is 'Z' or 'z')
        {
            return true;
        }

        // The only other RFC 3339 spelling is [+-]HH:MM, which occupies the final six characters.
        if (value.Length < 6)
        {
            return false;
        }

        var tail = value.AsSpan(value.Length - 6);

        return tail[0] is '+' or '-'
            && char.IsAsciiDigit(tail[1])
            && char.IsAsciiDigit(tail[2])
            && tail[3] == ':'
            && char.IsAsciiDigit(tail[4])
            && char.IsAsciiDigit(tail[5]);
    }
}

/// <summary>
/// Reads a UUID, yielding <see cref="Guid.Empty"/> for anything unreadable rather than throwing.
/// </summary>
/// <remarks>
/// <para>
/// Minimal API body binding does not surface a <see cref="JsonException"/> to
/// <c>UseExceptionHandler</c>: it catches the failure itself and writes a <c>400</c> carrying only
/// the generic problem body, with no <c>errors</c> member and no mention of which field was at
/// fault. So a caller who omitted a field got a precise, field-keyed answer, while a caller who
/// misspelled one got told merely that the request was bad - the vaguer answer landing on the
/// likelier mistake.
/// </para>
/// <para>
/// Yielding the sentinel the validator already rejects moves the failure out of the binder and
/// into the validation pipeline, where it comes back keyed by the wire field name and alongside
/// every other problem with the request. Parsing stays strict; only the reporting moves.
/// </para>
/// </remarks>
internal sealed class LenientGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out var value))
        {
            return value;
        }

        LenientScalar.Consume(ref reader);
        return Guid.Empty;
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // The same call the framework's own converter makes, so the response format is unchanged.
        writer.WriteStringValue(value);
    }
}

/// <summary>
/// Reads an RFC 3339 timestamp, yielding <see langword="default"/> for anything unreadable rather
/// than throwing. See <see cref="LenientGuidConverter"/> for why.
/// </summary>
/// <remarks>
/// Stricter than the framework's converter in one respect, deliberately: a timestamp with no UTC
/// offset is refused rather than silently resolved against the host's time zone. See
/// <see cref="LenientScalar.CarriesAnOffset"/>.
/// </remarks>
internal sealed class LenientDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && reader.TryGetDateTimeOffset(out var value)
            && LenientScalar.CarriesAnOffset(reader.GetString()))
        {
            return value;
        }

        LenientScalar.Consume(ref reader);
        return default;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Matches the framework's own converter: ISO 8601 extended, offset preserved verbatim.
        writer.WriteStringValue(value);
    }
}
