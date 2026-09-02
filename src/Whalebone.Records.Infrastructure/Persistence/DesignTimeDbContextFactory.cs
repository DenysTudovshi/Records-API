using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Whalebone.Records.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// Migrations are scaffolded from the model, so this never opens a connection. It
/// therefore needs a well-formed connection string and no credentials at all: a host and
/// a database name are enough for the provider to build the model. Supply
/// <c>Database__ConnectionString</c> when a command genuinely needs to reach a server,
/// such as <c>dotnet ef database update</c>.
/// </remarks>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RecordsDbContext>
{
    public RecordsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("Database__ConnectionString")
            ?? "Host=localhost;Database=records_design_time";

        var options = new DbContextOptionsBuilder<RecordsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new RecordsDbContext(options);
    }
}
