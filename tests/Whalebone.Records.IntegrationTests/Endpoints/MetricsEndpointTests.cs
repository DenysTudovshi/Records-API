using System.Text.RegularExpressions;

using Whalebone.Records.IntegrationTests.Infrastructure;

namespace Whalebone.Records.IntegrationTests.Endpoints;

/// <summary>
/// What matters about <c>/metrics</c> is not that it answers, but what it does and does not say.
/// </summary>
/// <remarks>
/// Every assertion here is about the presence of a series and its labels, never about a value.
/// The Prometheus registry is process-global and is not reset between tests, so any count read
/// here includes whatever the rest of the suite happened to do first.
/// </remarks>
public sealed class MetricsEndpointTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Duration = "microsoft_aspnetcore_hosting_http_server_request_duration";

    private const string Uuid =
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    [Fact]
    public async Task Metrics_ServesTheTextExpositionFormat()
    {
        using var response = await Client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        response.Content.Headers.ContentType.Parameters
            .Should().Contain(parameter => parameter.Name == "version" && parameter.Value == "0.0.4");
    }

    [Fact]
    public async Task Metrics_CountAndTimeBothRoutes_KeyedByTheRouteTemplate()
    {
        var externalId = Guid.NewGuid();
        using var saved = await Client.PostAsync("/save", Body(externalId.ToString()));
        using var fetched = await Client.GetAsync($"/{externalId}");

        var body = await ScrapeAsync();

        // .NET 8 emits no separate request counter: the histogram answers both questions, with
        // _count as the request count and _sum/_bucket as the duration.
        Series(body, $"{Duration}_count", "http_route=\"/save\"").Should().NotBeEmpty();
        Series(body, $"{Duration}_sum", "http_route=\"/save\"").Should().NotBeEmpty();
        Series(body, $"{Duration}_count", "http_route=\"/{id:guid}\"").Should().NotBeEmpty();
        Series(body, $"{Duration}_bucket", "http_route=\"/{id:guid}\"").Should().NotBeEmpty();
    }

    [Fact]
    public async Task Metrics_LabelNoSeriesWithAnExternalIdOrAnythingElseUnbounded()
    {
        var externalId = Guid.NewGuid();
        using var saved = await Client.PostAsync("/save", Body(externalId.ToString()));
        using var fetched = await Client.GetAsync($"/{externalId}");

        var body = await ScrapeAsync();

        body.Should().NotContain(externalId.ToString());

        // Any UUID at all, not just the one this test sent: an enrichment added later that starts
        // labelling by identity fails here even though this test never saw the value it used.
        Regex.IsMatch(body, Uuid).Should().BeFalse(
            "an external_id in a label is unbounded cardinality and personal data in one move");

        // Anchored to a label position deliberately. A bare NotContain("id") would fail on the
        // legitimate label value http_route="/{id:guid}".
        Regex.IsMatch(body, "[{,](external_id|id|name|email|date_of_birth)=").Should().BeFalse();
    }

    [Fact]
    public async Task Metrics_RecordAnUnmatchedRequestWithNoRouteLabelAtAll()
    {
        using var missing = await Client.GetAsync("/not-a-uuid");

        var body = await ScrapeAsync();

        // A request that matched no route contributes no http_route label, so a caller cannot
        // mint series by hammering random paths.
        var unhandled = Series(body, $"{Duration}_count", "aspnetcore_request_is_unhandled=\"True\"").ToArray();

        unhandled.Should().NotBeEmpty();
        unhandled.Should().OnlyContain(line => !line.Contains("http_route=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Metrics_BridgeExactlyTheTwoHostingInstruments_AndNoSecondAdapter()
    {
        var body = await ScrapeAsync();

        var connected = body.Split('\n')
            .Single(line => line.StartsWith("prometheus_net_meteradapter_instruments_connected ", StringComparison.Ordinal))
            .Split(' ')[1].Trim();

        // Microsoft.AspNetCore.Hosting publishes exactly two: http.server.active_requests and
        // http.server.request.duration. A larger number means either the meter filter has stopped
        // filtering, or a second MeterAdapter has been started over the process-global registry -
        // which silently multiplies every measurement by the number of hosts in the process.
        connected.Should().Be("2");
    }

    private async Task<string> ScrapeAsync()
    {
        using var response = await Client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private static IEnumerable<string> Series(string body, string name, string label) =>
        body.Split('\n')
            .Where(line => line.StartsWith(name + "{", StringComparison.Ordinal)
                           && line.Contains(label, StringComparison.Ordinal));
}
