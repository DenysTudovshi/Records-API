using Microsoft.AspNetCore.Mvc.Testing;

using Npgsql;

using Respawn;
using Respawn.Graph;

using Testcontainers.PostgreSql;

using Whalebone.Records.Infrastructure.Persistence;

namespace Whalebone.Records.IntegrationTests.Infrastructure;

/// <summary>
/// One Postgres container and one host for the whole collection, with the data reset
/// between tests.
/// </summary>
/// <remarks>
/// A container per test method is the obvious approach and costs seconds each time; one
/// shared container with no reset is faster but makes tests order-dependent, so a
/// "returns nothing" case only passes when the runner happens to schedule it first.
/// Respawn between tests buys isolation without paying for a new container.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Same tag as compose.yaml on purpose: passing against a different major than
    // production ships is a trap worth closing.
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("whalebone")
        .WithUsername("whalebone")
        .WithPassword("whalebone")
        .Build();

    private NpgsqlConnection _connection = null!;
    private Respawner _respawner = null!;
    private ApiFactory _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>Every log line the host has written, for the tests that assert on their contents.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>The running host's services, for assertions about how it was configured.</summary>
    public IServiceProvider Services => _factory.Services;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _factory = new ApiFactory(_database.GetConnectionString(), Logs);
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Redundant today, and deliberately kept. The entry point does run past builder.Build()
        // under WebApplicationFactory, and CreateClient does not return until it has got past its
        // own MigrateAsync - verified by delaying the entry point three seconds and watching the
        // tests still pass - which is why a run logs "Database schema is up to date." twice.
        // What this line buys is independence from Database:MigrateOnStartup. Turn that off and
        // the entry point skips migrating, and every test in the suite would fail on a missing
        // table for a reason that has nothing to do with the test. A migrated schema is this
        // fixture's own precondition, so the fixture asserts it rather than inheriting it.
        await DatabaseMigrator.MigrateAsync(_factory.Services);

        _connection = new NpgsqlConnection(_database.GetConnectionString());
        await _connection.OpenAsync();

        _respawner = await Respawner.CreateAsync(_connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Table("__EFMigrationsHistory")],
        });
    }

    public Task ResetAsync() => _respawner.ResetAsync(_connection);

    public async Task DisposeAsync()
    {
        Client?.Dispose();

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _database.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
