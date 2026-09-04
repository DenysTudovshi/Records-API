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
    [InlineData("1990-05-15T00:00:00+02:00")]
    [InlineData("1990-05-15T08:30:00-05:30")]
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
    public async Task Save_MissingFields_Returns400NamingEveryOneAsMissing()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await Client.PostAsync("/save", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var errors = await ErrorsByParameterAsync(response);

        errors.Keys.Should().BeEquivalentTo("external_id", "name", "email", "date_of_birth");

        // MISSING, not INVALID. The distinction is the vendor's own, and a field nobody sent is the
        // case it exists for; SaveRecordCommandValidatorTests holds the other side of it.
        errors.Values.Should().OnlyContain(entry =>
            entry.GetProperty("error").GetString() == "MISSING_PARAM_VALUE"
            && entry.GetProperty("error_code").GetInt32() == 22);
    }

    [Fact]
    public async Task Save_InvalidEmail_Returns400()
    {
        using var response = await Client.PostAsync("/save", Body(Guid.NewGuid().ToString(), email: "not-an-email"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var errors = await ErrorsByParameterAsync(response);

        errors.Should().ContainKey("email");
        errors["email"].GetProperty("error").GetString().Should().Be("INVALID_PARAM_VALUE");
        errors["email"].GetProperty("error_code").GetInt32().Should().Be(21);
    }

    [Fact]
    public async Task Save_MalformedJson_Returns400WithoutLeakingInternals()
    {
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        using var response = await Client.PostAsync("/save", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Npgsql").And.NotContain("StackTrace").And.NotContain("at Whalebone");

        // A body that could not be read has no parameter to blame, and the vendor's envelope marks
        // neither member required - so this carries message alone rather than an invented entry.
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        json.TryGetProperty("errors", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Save_MalformedExternalId_Returns400KeyedByExternalId()
    {
        using var response = await Client.PostAsync("/save", Body("not-a-uuid"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        // Exactly one entry: everything else in the body is valid, so a second would mean the
        // refused token had derailed the parse of a later member.
        var errors = await ErrorsByParameterAsync(response);

        errors.Keys.Should().BeEquivalentTo("external_id");
        errors["external_id"].GetProperty("error_code").GetInt32().Should().Be(21);
    }

    [Fact]
    public async Task Save_MalformedDateOfBirth_Returns400KeyedByDateOfBirth()
    {
        using var response = await Client.PostAsync(
            "/save", Body(Guid.NewGuid().ToString(), dateOfBirth: "01/01/2020"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Left to the binder this is the generic envelope: the caller learns the request was bad,
        // but not which field, and not that the format was the problem.
        var errors = await ErrorsByParameterAsync(response);

        errors.Keys.Should().BeEquivalentTo("date_of_birth");
        errors["date_of_birth"].GetProperty("error_code").GetInt32().Should().Be(21);
    }

    [Theory]
    [InlineData("2020-01-01")]
    [InlineData("2020-01-01T00:00:00")]
    public async Task Save_TimestampWithNoOffset_Is400_NotSilentlyGivenTheHostsOffset(string dateOfBirth)
    {
        using var response = await Client.PostAsync(
            "/save", Body(Guid.NewGuid().ToString(), dateOfBirth: dateOfBirth));

        // The framework's own converter accepts all of these and resolves them against the host's
        // time zone, so the same request would store +01:00 on a CET developer machine, +02:00 in
        // July, and +00:00 in the UTC container - then echo back an offset nobody sent. RFC 3339
        // makes the offset mandatory; this asserts the service does too.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var errors = await ErrorsByParameterAsync(response);

        errors.Keys.Should().BeEquivalentTo("date_of_birth");
    }

    [Fact]
    public async Task Save_CompositeWhereAScalarBelongs_Returns400WithoutDerailingTheParse()
    {
        const string json = """
            {"external_id":{"nested":"value"},"name":"some name","email":"email@email.com","date_of_birth":"2020-01-01T12:12:34+00:00"}
            """;
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await Client.PostAsync("/save", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // This is the only case that reaches the converters' Skip branch. If the composite were
        // not consumed, the reader would stall on it and the three valid members after it would
        // never bind - so the tell is that external_id is the *only* error reported.
        var errors = await ErrorsByParameterAsync(response);

        errors.Keys.Should().BeEquivalentTo("external_id");
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

    [Theory]
    [InlineData("SaveRecordRequest")]
    [InlineData("PersonRecordDto")]
    public async Task OpenApiDocument_MarksThePersonalDataFieldsWithTheVendorExtension(string schema)
    {
        using var response = await Client.GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var properties = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty(schema)
            .GetProperty("properties");

        // x-wb-encrypt is Whalebone's own extension for exactly this, and their only one. Three of
        // the four fields carry it; external_id is a caller-supplied opaque identifier and does not.
        foreach (var field in new[] { "name", "email", "date_of_birth" })
        {
            properties.GetProperty(field).TryGetProperty("x-wb-encrypt", out var marked)
                .Should().BeTrue("'{0}' is personal data and the document should say so", field);
            marked.GetBoolean().Should().BeTrue();
        }

        properties.GetProperty("external_id").TryGetProperty("x-wb-encrypt", out _)
            .Should().BeFalse("external_id is not personal data by itself");
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
