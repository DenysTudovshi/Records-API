using FluentValidation.TestHelper;

using Microsoft.Extensions.Time.Testing;

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
    public void Empty_external_id_is_rejected()
    {
        _validator.TestValidate(Command() with { ExternalId = Guid.Empty })
            .ShouldHaveValidationErrorFor("external_id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_name_is_rejected(string name)
    {
        _validator.TestValidate(Command() with { Name = name })
            .ShouldHaveValidationErrorFor("name");
    }

    [Fact]
    public void Over_long_name_is_rejected()
    {
        _validator.TestValidate(Command() with { Name = new string('x', 201) })
            .ShouldHaveValidationErrorFor("name");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@localhost")]
    [InlineData("user@example.")]
    public void Invalid_email_is_rejected(string email)
    {
        _validator.TestValidate(Command() with { Email = email })
            .ShouldHaveValidationErrorFor("email");
    }

    [Theory]
    [InlineData("email@email.com")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    public void Valid_email_is_accepted(string email)
    {
        _validator.TestValidate(Command() with { Email = email })
            .ShouldNotHaveValidationErrorFor("email");
    }

    [Fact]
    public void Missing_date_of_birth_is_rejected()
    {
        _validator.TestValidate(Command() with { DateOfBirth = default })
            .ShouldHaveValidationErrorFor("date_of_birth");
    }

    [Fact]
    public void Future_date_of_birth_is_rejected()
    {
        _validator.TestValidate(Command() with { DateOfBirth = Now.AddSeconds(1) })
            .ShouldHaveValidationErrorFor("date_of_birth");
    }

    [Fact]
    public void Errors_are_keyed_by_the_wire_field_names_not_the_clr_property_names()
    {
        var result = _validator.Validate(new SaveRecordCommand(Guid.Empty, "", "nope", default));

        result.Errors.Select(failure => failure.PropertyName)
            .Should().BeEquivalentTo("external_id", "name", "email", "date_of_birth");
    }

    private static SaveRecordCommand Command() => new(
        Guid.NewGuid(),
        "some name",
        "email@email.com",
        new DateTimeOffset(2020, 1, 1, 12, 12, 34, TimeSpan.Zero));
}
