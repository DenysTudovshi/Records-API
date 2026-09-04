using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;

using Testcontainers.PostgreSql;

using Whalebone.Records.IntegrationTests.Infrastructure;

namespace Whalebone.Records.IntegrationTests.EndToEnd;

/// <summary>
/// The application run as the whole program: the production image, its real entrypoint,
/// its real environment-variable parsing, a real Postgres reached over a real Docker
/// network, and requests made over a real TCP socket.
/// </summary>
/// <remarks>
/// <para>
/// The other integration tests use <c>WebApplicationFactory</c>, which is fast, and which does
/// run the entry point in full - migrations included. What it never does is bind a socket:
/// Kestrel never starts, requests travel over an in-memory transport, and the image's own
/// entrypoint, environment parsing and network are all absent. This class closes that gap:
/// nothing here is substituted, and no test-only code path exists in the app.
/// </para>
/// <para>
/// CI sets <c>WHALEBONE_IMAGE</c> to the tag it just built, so the same code that proves
/// the requirement also gates the publish. Locally the image is built from the repository
/// Dockerfile on the fly.
/// </para>
/// </remarks>
[Trait("Category", "EndToEnd")]
public sealed class ContainerizedAppTests : IAsyncLifetime
{
    private const ushort ApiPort = 8080;
    private const string DatabaseAlias = "db";

    private readonly INetwork _network = new NetworkBuilder().Build();

    private PostgreSqlContainer _database = null!;
    private IFutureDockerImage? _builtImage;
    private IContainer _api = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();

        _database = new PostgreSqlBuilder("postgres:16-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases(DatabaseAlias)
            .WithDatabase("whalebone")
            .WithUsername("whalebone")
            .WithPassword("whalebone")
            .Build();

        await _database.StartAsync();

        var apiBuilder = new ContainerBuilder(await ResolveApiImageAsync())
            .WithNetwork(_network)
            // Supplied exactly as compose.yaml supplies them, and parsed by the real
            // IConfiguration pipeline inside the container.
            .WithEnvironment(
                "Database__ConnectionString",
                $"Host={DatabaseAlias};Port=5432;Database=whalebone;Username=whalebone;Password=whalebone")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
            .WithPortBinding(ApiPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/health/ready").ForPort(ApiPort)));

        _api = apiBuilder.Build();
        await _api.StartAsync();

        _client = new HttpClient
        {
            BaseAddress = new Uri($"http://{_api.Hostname}:{_api.GetMappedPublicPort(ApiPort)}"),
        };
    }

    /// <summary>
    /// The tag CI just built, or a fresh build from the repository Dockerfile when running
    /// locally. Either way it is the production image, never a test-only variant.
    /// </summary>
    private async Task<IImage> ResolveApiImageAsync()
    {
        var prebuilt = Environment.GetEnvironmentVariable("WHALEBONE_IMAGE");
        if (!string.IsNullOrWhiteSpace(prebuilt))
        {
            return new DockerImage(prebuilt);
        }

        _builtImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(RepositoryRoot.Find())
            .WithDockerfile("Dockerfile")
            .WithName("records-api:endtoend")
            .WithCleanUp(false)
            .Build();

        await _builtImage.CreateAsync();
        return _builtImage;
    }

    [Fact]
    public async Task SaveThenGet_RoundTripsOverRealHttp_PreservingTheOffset()
    {
        var externalId = Guid.NewGuid();
        var body = $$"""
            {"external_id":"{{externalId}}","name":"Ada Lovelace","email":"ada@example.com","date_of_birth":"1815-12-10T00:00:00+02:00"}
            """;

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var saved = await _client.PostAsync("/save", content);

        saved.StatusCode.Should().Be(HttpStatusCode.Created);
        saved.Headers.Location!.OriginalString.Should().Be($"/{externalId}");

        using var fetched = await _client.GetAsync($"/{externalId}");

        fetched.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("external_id").GetString().Should().Be(externalId.ToString());
        json.GetProperty("name").GetString().Should().Be("Ada Lovelace");
        json.GetProperty("email").GetString().Should().Be("ada@example.com");

        // Survived JSON binding, the domain decomposition, a timestamptz column, the
        // reconstruction on read, and serialisation - across a process and a network.
        json.GetProperty("date_of_birth").GetString().Should().Be("1815-12-10T00:00:00+02:00");
    }

    [Fact]
    public async Task Get_UnknownId_Returns404OverRealHttp()
    {
        using var response = await _client.GetAsync($"/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheContainerAppliesItsOwnMigrationsOnStartup()
    {
        // The container reached a healthy /health/ready before any test ran, which is only
        // possible if the app connected to an empty database and migrated it itself.
        using var response = await _client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MissingBody_Returns400OverRealHttp()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await _client.PostAsync("/save", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        // The media type alone proves nothing now - a success returns application/json too. This is
        // the vendor envelope, over a real socket, out of the production image.
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var entries = json.GetProperty("errors").EnumerateArray().ToArray();

        json.GetProperty("message").GetString().Should().Be("Request validation failed");
        entries.Select(entry => entry.GetProperty("parameter").GetString())
            .Should().BeEquivalentTo("external_id", "name", "email", "date_of_birth");
        entries.Should().OnlyContain(entry => entry.GetProperty("error_code").GetInt32() == 22);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        await _database.DisposeAsync();

        if (_builtImage is not null)
        {
            await _builtImage.DisposeAsync();
        }

        await _network.DisposeAsync();
    }
}
