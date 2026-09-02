using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

using Whalebone.Records.IntegrationTests.Infrastructure;
using Whalebone.Records.Infrastructure.Persistence;

namespace Whalebone.Records.IntegrationTests.DataProtection;

/// <summary>
/// The payload is a name, an email address and a date of birth. Handling it correctly is not
/// worth much if nothing would notice it stopping.
/// </summary>
/// <remarks>
/// The <c>external_id</c> is deliberately absent from these assertions. It is a caller-supplied
/// opaque identifier and it legitimately appears in the hosting scope's <c>RequestPath</c> for
/// <c>GET /{id}</c>; asserting it away would mean asserting the framework's request logging away.
/// </remarks>
public sealed class PersonalDataTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Name = "Zzqqxx Piinamemarker";
    private const string Email = "piiemailmarker@zzqqxx-example.test";
    private const string DateOfBirth = "1971-03-04T02:06:07+02:00";
    private const string DateMarker = "1971-03-04";

    private const string EfCommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    [Fact]
    public async Task Logs_NeverCarryTheName_TheEmail_OrTheDateOfBirth()
    {
        var externalId = Guid.NewGuid();
        Logs.Clear();

        using var saved = await Client.PostAsync(
            "/save", Body(externalId.ToString(), Name, Email, DateOfBirth));
        saved.StatusCode.Should().Be(HttpStatusCode.Created);

        using var fetched = await Client.GetAsync($"/{externalId}");
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);

        var captured = Logs.Snapshot();

        // Three ascending guards, so this can never become a statement about an empty list. The
        // interesting failure is not "the assertion broke" but "the assertion stopped being made":
        // pin the levels wrong and every line below passes while nothing is checked.
        captured.Should().NotBeEmpty("the capture must actually be receiving log lines");
        captured.Should().Contain(entry => entry.Category == EfCommandCategory,
            "the EF Core command channel is the one a leak would travel on");
        captured.Should().Contain(entry => entry.Message.Contains("INSERT INTO person_records", StringComparison.Ordinal),
            "the write that carries the personal data must be among the lines examined");

        foreach (var marker in new[] { Name, Email, DateMarker })
        {
            var leaked = captured
                .Where(entry => entry.AllText().Any(text => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            leaked.Should().BeEmpty(
                "'{0}' is personal data and must not reach a log line, in the message, the state or a scope. Leaked via: {1}",
                marker,
                string.Join(" | ", leaked.Select(entry => $"{entry.Category}: {entry.Message}")));
        }
    }

    [Fact]
    public void SensitiveDataLogging_IsOff()
    {
        using var scope = Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<RecordsDbContext>>();
        var core = options.FindExtension<CoreOptionsExtension>();

        core.Should().NotBeNull();

        // Off by default, and asserted rather than trusted: turning it on makes EF Core write every
        // parameter value verbatim, which is the whole payload, on the Information channel.
        core!.IsSensitiveDataLoggingEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ValidationFailure_NamesTheFieldWithoutEchoingTheRejectedValue()
    {
        using var response = await Client.PostAsync(
            "/save", Body(Guid.NewGuid().ToString(), Name, "piiemailmarker-not-an-email", DateOfBirth));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();

        // The vendor's own API echoes the rejected input back as `value`. Here the rejected input
        // is personal data, and an error body is among the least controlled things a service
        // emits - it reaches the caller, their logs, and frequently a screenshot.
        body.Should().NotContain("piiemailmarker");
        body.Should().NotContain(Name);

        // Still useful, though: the field is named, in its wire spelling.
        JsonDocument.Parse(body).RootElement
            .GetProperty("errors").TryGetProperty("email", out _).Should().BeTrue();
    }

    [Fact]
    public async Task SuccessfulResponses_ReturnTheFourContractFieldsAndNothingElse()
    {
        var externalId = Guid.NewGuid();
        using var saved = await Client.PostAsync(
            "/save", Body(externalId.ToString(), Name, Email, DateOfBirth));

        using var fetched = await Client.GetAsync($"/{externalId}");
        var json = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync()).RootElement;

        // The surrogate key and the audit timestamps are internal, and a record's storage history
        // is not the caller's to read.
        json.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("external_id", "name", "email", "date_of_birth");
    }
}
