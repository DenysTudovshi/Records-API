using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Whalebone.Records.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time. It never connects - migrations are
/// scaffolded from the model - so the placeholder connection string is enough, and
/// generating a migration needs no running database.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RecordsDbContext>
{
    public RecordsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("Database__ConnectionString")
            ?? "Host=localhost;Port=5432;Database=whalebone;Username=whalebone;Password=whalebone";

        var options = new DbContextOptionsBuilder<RecordsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new RecordsDbContext(options);
    }
}
