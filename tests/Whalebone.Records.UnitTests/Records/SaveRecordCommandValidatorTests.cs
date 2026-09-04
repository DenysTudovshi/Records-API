using FluentValidation.TestHelper;

using Microsoft.Extensions.Time.Testing;

using Whalebone.Records.Application.Abstractions;
using Whalebone.Records.Application.Records.Save;

namespace Whalebone.Records.UnitTests.Records;

public sealed class SaveRecordCommandValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SaveRecordCommandValidator _validator =
        new(new FakeTimeProvider(Now));

    [Fact]
    public void Valid_command_passes()
    {
        _validator.TestValidate(Command()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Blank_name_is_rejected()
    {
        _validator.TestValidate(Command() with { Name = "   " })
            .ShouldHaveValidationErrorFor("name");
    }

    [Fact]
    public void Over_long_name_is_rejected()
    {
        _validator.TestValidate(Command() with { Name = new string('x', 201) })
            .ShouldHaveValidationErrorFor("name");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("user@")]
    // MailAddress accepts this one; the dotted-host rule is what rejects it.
    [InlineData("user@localhost")]
    public void Invalid_email_is_rejected(string email)
    {
        _validator.TestValidate(Command() with { Email = email })
            .ShouldHaveValidationErrorFor("email");
    }

    [Fact]
    public void Valid_email_is_accepted()
    {
        _validator.TestValidate(Command() with { Email = "first.last+tag@sub.example.co.uk" })
            .ShouldNotHaveValidationErrorFor("email");
    }

    [Fact]
    public void Future_date_of_birth_is_rejected()
    {
        _validator.TestValidate(Command() with { DateOfBirth = Now.AddSeconds(1) })
            .ShouldHaveValidationErrorFor("date_of_birth");
    }

    [Fact]
    public void Absent_values_are_reported_as_missing()
    {
        var result = _validator.Validate(new SaveRecordCommand(null, null, null, null));

        result.Errors.Select(failure => failure.PropertyName)
            .Should().BeEquivalentTo("external_id", "name", "email", "date_of_birth");
        result.Errors.Should().OnlyContain(failure => failure.ErrorCode == ValidationErrorCodes.Missing);
    }

    [Fact]
    public void Present_but_unusable_values_are_reported_as_invalid()
    {
        // Every one of these was sent by the caller - an all-zero uuid, a blank name, a string that
        // is not an address, and the sentinel the converter yields for an unreadable timestamp.
        var result = _validator.Validate(
            new SaveRecordCommand(Guid.Empty, "", "nope", default(DateTimeOffset)));

        result.Errors.Select(failure => failure.PropertyName)
            .Should().BeEquivalentTo("external_id", "name", "email", "date_of_birth");
        result.Errors.Should().OnlyContain(failure => failure.ErrorCode == ValidationErrorCodes.Invalid);
    }

    private static SaveRecordCommand Command() => new(
        Guid.NewGuid(),
        "some name",
        "email@email.com",
        new DateTimeOffset(2020, 1, 1, 12, 12, 34, TimeSpan.Zero));
}
