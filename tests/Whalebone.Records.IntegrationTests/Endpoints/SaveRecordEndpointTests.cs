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

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("3fa85f64-5717-4562-b3fc")]
    public async Task Save_MalformedExternalId_Returns400KeyedByExternalId(string externalId)
    {
        using var response = await Client.PostAsync("/save", Body(externalId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // Exactly one key: everything else in the body is valid, so a second entry would mean the
        // refused token had derailed the parse of a later member.
        json.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("external_id");
    }

    [Theory]
    [InlineData("01/01/2020")]
    [InlineData("yesterday")]
    [InlineData("2020-13-45T99:99:99+00:00")]
    public async Task Save_MalformedDateOfBirth_Returns400KeyedByDateOfBirth(string dateOfBirth)
    {
        using var response = await Client.PostAsync(
            "/save", Body(Guid.NewGuid().ToString(), dateOfBirth: dateOfBirth));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Left to the binder this is the generic problem body: the caller learns the request was
        // bad, but not which field, and not that the format was the problem.
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("date_of_birth");
    }

    [Theory]
    [InlineData("2020-01-01")]
    [InlineData("2020-06-01")]
    [InlineData("2020-01-01T00:00:00")]
    [InlineData("2020-01-01T00:00:00.123")]
    public async Task Save_TimestampWithNoOffset_Is400_NotSilentlyGivenTheHostsOffset(string dateOfBirth)
    {
        using var response = await Client.PostAsync(
            "/save", Body(Guid.NewGuid().ToString(), dateOfBirth: dateOfBirth));

        // The framework's own converter accepts all of these and resolves them against the host's
        // time zone, so the same request would store +01:00 on a CET developer machine, +02:00 in
        // July, and +00:00 in the UTC container - then echo back an offset nobody sent. RFC 3339
        // makes the offset mandatory; this asserts the service does too.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("date_of_birth");
    }

    [Theory]
    [InlineData("{\"nested\":\"value\"}")]
    [InlineData("[1,2,3]")]
    public async Task Save_CompositeWhereAScalarBelongs_Returns400WithoutDerailingTheParse(string composite)
    {
        var json = $$"""
            {"external_id":{{composite}},"name":"some name","email":"email@email.com","date_of_birth":"2020-01-01T12:12:34+00:00"}
            """;
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await Client.PostAsync("/save", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // This is the only case that reaches the converters' Skip branch. If the composite were
        // not consumed, the reader would stall on it and the three valid members after it would
        // never bind - so the tell is that external_id is the *only* error reported.
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("external_id");
    }

    [Fact]
    public async Task Save_MalformedScalars_AreReportedAlongsideEveryOtherProblem()
    {
        using var response = await Client.PostAsync(
            "/save", Body("not-a-uuid", name: "", email: "nope", dateOfBirth: "01/01/2020"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // The point of moving the failure out of the binder: one response naming all four
        // problems, rather than a 400 that stops at the first token it could not read.
        json.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("external_id", "name", "email", "date_of_birth");
    }

    [Fact]
    public async Task OpenApiDocument_StillAdvertisesTheScalarFormats()
    {
        using var response = await Client.GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var properties = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("SaveRecordRequest")
            .GetProperty("properties");

        // What this pins is the document itself: snake_case keys, and the two scalars still
        // described by their formats rather than as bare strings. The regression it catches is a
        // DTO that stops being typed - swapping Guid?/DateTimeOffset? for string?, the other
        // obvious way to move malformed input out of the binder, silently costs both formats.
        //
        // It deliberately does NOT claim to pin where the lenient converters are registered.
        // Adding them to Mvc.JsonOptions as well leaves this document byte-identical on
        // Swashbuckle 8.1.4 - measured by doing it and watching this test still pass.
        properties.GetProperty("external_id").GetProperty("format").GetString().Should().Be("uuid");
        properties.GetProperty("date_of_birth").GetProperty("format").GetString().Should().Be("date-time");
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
