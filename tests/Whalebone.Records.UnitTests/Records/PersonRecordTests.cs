using System.Globalization;

using Whalebone.Records.Application.Domain;

namespace Whalebone.Records.UnitTests.Records;

/// <summary>
/// The date of birth is stored as a UTC instant plus the caller's offset. These tests pin
/// both halves of that bargain: the instant must be correct and comparable, and the
/// reconstructed value must be indistinguishable from what the caller supplied.
/// </summary>
public sealed class PersonRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2020-01-01T12:12:34+00:00")]
    [InlineData("1990-05-15T08:30:00-05:30")]
    [InlineData("2000-06-30T23:59:59+14:00")]
    public void DateOfBirth_RoundTripsTheCallersOffsetExactly(string input)
    {
        var supplied = DateTimeOffset.Parse(input, CultureInfo.InvariantCulture);

        var record = PersonRecord.Create(Guid.NewGuid(), "n", "e@example.com", supplied, Now);

        record.DateOfBirth.Should().Be(supplied);
        record.DateOfBirth.Offset.Should().Be(supplied.Offset);
        record.DateOfBirth.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
            .Should().Be(supplied.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DateOfBirthUtc_IsTheAbsoluteInstant_SoTheColumnStaysSortable()
    {
        var supplied = new DateTimeOffset(1990, 5, 15, 0, 0, 0, TimeSpan.FromHours(2));

        var record = PersonRecord.Create(Guid.NewGuid(), "n", "e@example.com", supplied, Now);

        record.DateOfBirthUtc.Should().Be(new DateTime(1990, 5, 14, 22, 0, 0, DateTimeKind.Utc));
        record.DateOfBirthUtc.Kind.Should().Be(DateTimeKind.Utc);
        record.DateOfBirthOffsetMinutes.Should().Be(120);
    }

    [Fact]
    public void Update_ReplacesTheMutableFieldsAndKeepsCreatedAt()
    {
        var record = PersonRecord.Create(
            Guid.NewGuid(), "old", "old@example.com", Now.AddYears(-30), Now);
        var createdAt = record.CreatedAtUtc;
        var later = Now.AddDays(1);

        record.Update("new", "new@example.com", Now.AddYears(-20), later);

        record.Name.Should().Be("new");
        record.Email.Should().Be("new@example.com");
        record.CreatedAtUtc.Should().Be(createdAt, "an update must not rewrite when the record first appeared");
        record.UpdatedAtUtc.Should().Be(later.UtcDateTime);
    }
}
