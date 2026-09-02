using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Whalebone.Records.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real application and points it at a throwaway Postgres container.
/// </summary>
/// <remarks>
/// Configuration is overridden, not services. Replacing the <c>DbContextOptions</c>
/// registration - the common shortcut - would mean the registration under test is the
/// one thing never exercised. Here the production Npgsql provider, retry policy and
/// options validation all run exactly as they do in the container.
/// </remarks>
internal sealed class ApiFactory(string connectionString, CapturingLoggerProvider logs)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Production, so appsettings.Development.json cannot quietly supply a different database.
        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = connectionString,
            }));

        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(logs);

            // Scoped to this provider, deliberately. appsettings.json pins Microsoft.AspNetCore
            // and the EF Core command channel to Warning, and a capture that inherits those pins
            // records nothing at all - at which point "no log line contains this email address"
            // is a statement about an empty list. Lifting the level globally instead would change
            // what every other test runs against; this way only the capture sees everything.
            logging.AddFilter<CapturingLoggerProvider>(category: null, level: LogLevel.Trace);
        });
    }
}
