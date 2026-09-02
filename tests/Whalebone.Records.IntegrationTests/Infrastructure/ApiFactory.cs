using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
internal sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
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
    }
}
