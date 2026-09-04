using Whalebone.Records.IntegrationTests.Infrastructure;

namespace Whalebone.Records.IntegrationTests.Endpoints;

/// <summary>
/// The correlation id has to survive three things: a request that never mentions it, a caller
/// who supplies their own, and the error path - where the exception middleware clears every
/// response header before the body is written.
/// </summary>
public sealed class CorrelationIdTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string HeaderName = "X-Request-Id";

    [Fact]
    public async Task Response_CarriesAGeneratedRequestId_WhenTheCallerSendsNone()
    {
        using var response = await Client.GetAsync("/health/live");

        RequestIdOf(response).Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(RequestIdOf(response), out _)
            .Should().BeTrue("a generated id is a UUID, matching the shape the vendor's API returns");
    }

    [Fact]
    public async Task Response_EchoesASuppliedRequestId_WhenItCouldPlausiblyBeOne()
    {
        const string supplied = "2ef1a5ed-e549-4e37-a1c7-3584087184ec";

        using var response = await SendWithRequestId("/health/live", supplied);

        RequestIdOf(response).Should().Be(supplied);
    }

    [Theory]
    [InlineData("newline\r\ninjected")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Response_ReplacesASuppliedRequestId_WhenItIsMalformedOrOversized(string supplied)
    {
        using var response = await SendWithRequestId("/health/live", supplied);

        var echoed = RequestIdOf(response);

        // Replaced, not reflected: nothing the caller sent may reach the response header, and
        // the id that does come back is one this service minted.
        echoed.Should().NotBe(supplied);
        echoed.Should().NotContain(" ").And.NotContain("<").And.NotContain("\"");
        Guid.TryParse(echoed, out _).Should().BeTrue();
    }

    [Fact]
    public async Task ValidationFailure_CarriesTheRequestIdInTheHeaderAndTheBody()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("/save", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The exception middleware clears every response header before the handler writes the
        // body. This assertion is the whole reason the header is set from OnStarting.
        var header = RequestIdOf(response);
        header.Should().NotBeNullOrWhiteSpace();

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // snake_case, and asserted rather than assumed: the key comes from the global naming policy
        // applied to ErrorResponse.RequestId, which is one policy change away from reading requestId.
        json.TryGetProperty("request_id", out var requestId).Should().BeTrue();
        requestId.GetString().Should().Be(header);
        json.TryGetProperty("requestId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Responses_CarryADistinctRequestIdPerRequest()
    {
        using var first = await Client.GetAsync("/health/live");
        using var second = await Client.GetAsync("/health/live");

        RequestIdOf(first).Should().NotBe(RequestIdOf(second));
    }

    private async Task<HttpResponseMessage> SendWithRequestId(string path, string requestId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        // Without validation on purpose: several of these values are exactly what HttpClient
        // would refuse to send, and surviving them is the server's job.
        request.Headers.TryAddWithoutValidation(HeaderName, requestId);

        // Awaited rather than returned: the request message must outlive the send.
        return await Client.SendAsync(request);
    }

    private static string? RequestIdOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues(HeaderName, out var values) ? values.Single() : null;
}
