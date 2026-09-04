using Whalebone.Records.IntegrationTests.Infrastructure;

namespace Whalebone.Records.IntegrationTests.Endpoints;

public sealed class GetRecordEndpointTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Get_ReturnsExactlyTheFourContractFields_InSnakeCase()
    {
        var externalId = Guid.NewGuid();
        using var saved = await Client.PostAsync("/save", Body(externalId.ToString()));
        saved.StatusCode.Should().Be(HttpStatusCode.Created);

        using var response = await Client.GetAsync($"/{externalId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // Pins the wire format. A regression in the snake_case policy fails here, and so
        // does any internal field leaking out - the surrogate key, the audit timestamps.
        json.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("external_id", "name", "email", "date_of_birth");

        json.GetProperty("external_id").GetString().Should().Be(externalId.ToString());
        json.GetProperty("name").GetString().Should().Be("some name");
        json.GetProperty("email").GetString().Should().Be("email@email.com");
        json.GetProperty("date_of_birth").GetString().Should().Be("2020-01-01T12:12:34+00:00");
    }

    [Fact]
    public async Task Get_UnknownId_Returns404WithTheErrorEnvelope()
    {
        using var response = await Client.GetAsync($"/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        // A 404 blames no parameter, so the envelope carries message alone. The vendor documents no
        // 404 at all - 200, 400, 401, 429, 500 and 503 are the whole published set - and their
        // envelope marks neither member required, so this is the closest reading their schema allows.
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        json.TryGetProperty("errors", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Health_Ready_ReportsHealthyWhenTheDatabaseIsReachable()
    {
        using var response = await Client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_Live_DoesNotDependOnTheDatabase()
    {
        using var response = await Client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
