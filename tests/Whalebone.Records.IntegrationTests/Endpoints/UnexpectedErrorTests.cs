using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Whalebone.Records.IntegrationTests.Infrastructure;

namespace Whalebone.Records.IntegrationTests.Endpoints;

/// <summary>
/// The <c>500</c> arm - the only status besides <c>400</c> for which the vendor publishes a body
/// schema, and the one place the two schemas differ.
/// </summary>
/// <remarks>
/// Driven by pointing the real host at a database that is not there, rather than by a test-only
/// endpoint that throws: the exception has to travel the production path to prove the production
/// shape. Startup migration is off and the retry count is zero, so the failure is immediate rather
/// than eight backoffs later.
/// <para>
/// In the Postgres collection despite needing no database, purely to be serialised against it. This
/// class stands up a second host, and the Prometheus registry is process-global: while two hosts
/// are alive the meter adapter reports two hosting meters' worth of instruments, which is correct
/// but would make MetricsEndpointTests' exact count race. Joining the collection is what keeps that
/// assertion exact instead of loosening it.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class UnexpectedErrorTests : IAsyncLifetime
{
    private UnreachableDatabaseFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new UnreachableDatabaseFactory();
        _client = _factory.CreateClient();

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AnUnhandledFailure_Returns500InTheBareErrorShape()
    {
        using var response = await _client.PostAsync("/save", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // Flat, not wrapped. The vendor attaches the bare `error` schema to 500 and 503 on every one
        // of their operations, and reserves the {message, errors[]} envelope for 400. Wrapping this
        // would fail their schema on two required members, error and error_code.
        json.GetProperty("error").GetString().Should().Be("UNEXPECTED_ERROR");
        json.GetProperty("error_code").GetInt32().Should().Be(10);
        json.GetProperty("message").GetString().Should().Be("Unexpected error occurred.");
        json.TryGetProperty("errors", out _).Should().BeFalse();

        json.GetProperty("request_id").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TheFailureBody_SaysNothingAboutWhatActuallyBroke()
    {
        using var response = await _client.PostAsync("/save", ValidBody());

        var body = await response.Content.ReadAsStringAsync();

        // A 500 is the easiest place to leak a connection string, a host name or a stack trace.
        body.Should().NotContain("Npgsql").And.NotContain("127.0.0.1").And.NotContain("Password");
        body.Should().NotContain("StackTrace").And.NotContain("at Whalebone");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    private static StringContent ValidBody()
    {
        var json = $$"""
            {"external_id":"{{Guid.NewGuid()}}","name":"some name","email":"email@email.com","date_of_birth":"2020-01-01T12:12:34+00:00"}
            """;

        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    /// <summary>The real host, pointed at a port nothing is listening on.</summary>
    private sealed class UnreachableDatabaseFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment("Production");

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Port 1 is reserved, so the connection is refused rather than timing out.
                    ["Database:ConnectionString"] =
                        "Host=127.0.0.1;Port=1;Database=absent;Username=x;Password=y;Timeout=1;Command Timeout=1",

                    // Without this the host would fail to start instead of failing to serve, and the
                    // 500 path would never be reached.
                    ["Database:MigrateOnStartup"] = "false",
                    ["Database:MaxRetryCount"] = "0",
                }));
        }
    }
}
