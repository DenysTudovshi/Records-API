using Whalebone.Records.IntegrationTests.Infrastructure;

namespace Whalebone.Records.IntegrationTests.Endpoints;

public sealed class SaveRecordEndpointTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Save_NewRecord_Returns201WithLocationPointingAtTheExternalId()
    {
        var externalId = Guid.NewGuid();

        using var response = await Client.PostAsync("/save", Body(externalId.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.OriginalString.Should().Be($"/{externalId}");
    }

    [Fact]
    public async Task Save_ExistingExternalId_Returns200AndReplacesTheRecord()
    {
        var externalId = Guid.NewGuid();
        using var created = await Client.PostAsync("/save", Body(externalId.ToString(), name: "first"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using var updated = await Client.PostAsync("/save", Body(externalId.ToString(), name: "second"));

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        using var fetched = await Client.GetAsync($"/{externalId}");
        var json = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("name").GetString().Should().Be("second");
    }

    [Fact]
    public async Task Save_ConcurrentDuplicates_ConvergeOnASingleRecord()
    {
        var externalId = Guid.NewGuid();

        // Several writers all read "absent" before any of them writes, so all but one lose
        // the race on the unique index. Converging on an update, rather than surfacing a
        // 500, is the whole point of catching the unique violation.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(index =>
                Client.PostAsync("/save", Body(externalId.ToString(), name: $"writer-{index}"))));

        try
        {
            responses.Should().OnlyContain(response =>
                response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK);

            responses.Count(response => response.StatusCode == HttpStatusCode.Created)
                .Should().Be(1, "exactly one writer may create the record");

            using var fetched = await Client.GetAsync($"/{externalId}");
            fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("2020-01-01T12:12:34+00:00")]
    [InlineData("1990-05-15T00:00:00+02:00")]
    [InlineData("1990-05-15T08:30:00-05:30")]
    [InlineData("1815-12-10T00:00:00+01:00")]
    public async Task Save_PreservesTheSuppliedUtcOffsetExactly(string dateOfBirth)
    {
        var externalId = Guid.NewGuid();

        using var saved = await Client.PostAsync("/save", Body(externalId.ToString(), dateOfBirth: dateOfBirth));
        saved.StatusCode.Should().Be(HttpStatusCode.Created);

        using var fetched = await Client.GetAsync($"/{externalId}");
        var json = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync()).RootElement;

        json.GetProperty("date_of_birth").GetString().Should().Be(dateOfBirth);
    }

    [Fact]
    public async Task Save_MissingFields_Returns400ProblemDetailsKeyedByWireFieldNames()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await Client.PostAsync("/save", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        json.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("external_id", "name", "email", "date_of_birth");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("user@localhost")]
    public async Task Save_InvalidEmail_Returns400(string email)
    {
        using var response = await Client.PostAsync("/save", Body(Guid.NewGuid().ToString(), email: email));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("errors").TryGetProperty("email", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Save_MalformedJson_Returns400WithoutLeakingInternals()
    {
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        using var response = await Client.PostAsync("/save", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Npgsql").And.NotContain("StackTrace").And.NotContain("at Whalebone");
    }

    [Fact]
    public async Task Save_FutureDateOfBirth_Returns400()
    {
        var future = DateTimeOffset.UtcNow.AddYears(1)
            .ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

        using var response = await Client.PostAsync("/save", Body(Guid.NewGuid().ToString(), dateOfBirth: future));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
