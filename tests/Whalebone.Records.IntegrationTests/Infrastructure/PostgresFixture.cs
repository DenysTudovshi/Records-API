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

    public async Task InitializeAsync()
    {
        await _database.StartAsync();

        _factory = new ApiFactory(_database.GetConnectionString());
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // WebApplicationFactory captures the host at Build() and unwinds out of the entry
        // point, so the MigrateAsync call in Program.cs never reaches this host. Invoking
        // the very same production migrator here keeps the schema honest; the startup path
        // itself is covered by the end-to-end container test.
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
