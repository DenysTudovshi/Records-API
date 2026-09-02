using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace Whalebone.Records.Infrastructure.Persistence;

/// <summary>Applies pending migrations during startup.</summary>
public static class DatabaseMigrator
{
    /// <summary>
    /// Arbitrary but fixed key identifying this service's schema lock. Any value works
    /// as long as every replica agrees on it.
    /// </summary>
    private const long AdvisoryLockKey = 0x5748_414C_4542_4F4EL; // "WHALEBON"

    /// <summary>
    /// Migrates under a PostgreSQL session-level advisory lock, so that N replicas
    /// starting at once serialise instead of racing each other into a half-applied schema.
    /// </summary>
    /// <remarks>
    /// Failures are rethrown deliberately. An app that starts "successfully" against a
    /// schema-less database just serves 500s, which is a worse outcome than refusing to boot.
    /// </remarks>
    public static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DatabaseMigrator));

        if (!options.MigrateOnStartup)
        {
            logger.LogInformation("Startup migration disabled by configuration; skipping.");
            return;
        }

        var dbContext = scope.ServiceProvider.GetRequiredService<RecordsDbContext>();

        await using var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, "SELECT pg_advisory_lock(@key)", cancellationToken).ConfigureAwait(false);
        try
        {
            dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(options.MigrationTimeoutSeconds));
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Database schema is up to date.");
        }
        finally
        {
            await ExecuteAsync(connection, "SELECT pg_advisory_unlock(@key)", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }
}
