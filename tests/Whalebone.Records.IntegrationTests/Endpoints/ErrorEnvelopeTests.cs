using Whalebone.Records.IntegrationTests.Infrastructure;

namespace Whalebone.Records.IntegrationTests.Endpoints;

/// <summary>
/// The statuses the pipeline sets without writing a body, which the status-code handler fills in.
/// </summary>
/// <remarks>
/// None of these is a status the vendor publishes a body schema for, so each carries the envelope
/// with <c>message</c> alone - there is no parameter to name and nothing to put in an array. What
/// is being pinned is that they carry the envelope at all: before this handler existed they were
/// bodyless, or a second error shape.
/// </remarks>
public sealed class ErrorEnvelopeTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task UnroutedPath_Returns404InTheEnvelope()
    {
        using var response = await Client.GetAsync("/no-such-route");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await ShouldBeAMessageOnlyEnvelope(response);
    }

    [Fact]
    public async Task WrongMethodOnAKnownRoute_Returns405InTheEnvelope()
    {
        using var response = await Client.DeleteAsync("/save");

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);

        await ShouldBeAMessageOnlyEnvelope(response);
    }

    [Fact]
    public async Task WrongContentType_Returns415InTheEnvelope()
    {
        using var content = new StringContent("not json", Encoding.UTF8, "text/plain");

        using var response = await Client.PostAsync("/save", content);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);

        await ShouldBeAMessageOnlyEnvelope(response);
    }

    [Fact]
    public async Task EveryErrorBody_CarriesTheRequestIdFromTheHeader()
    {
        using var response = await Client.GetAsync("/no-such-route");

        var header = response.Headers.GetValues("X-Request-Id").Single();

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("request_id").GetString().Should().Be(header);
    }

    private static async Task ShouldBeAMessageOnlyEnvelope(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        json.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        json.GetProperty("request_id").GetString().Should().NotBeNullOrWhiteSpace();

        // No errors[] and no bare `error`: this is neither a parameter failure nor a fault.
        json.TryGetProperty("errors", out _).Should().BeFalse();
        json.TryGetProperty("error", out _).Should().BeFalse();
    }
}
