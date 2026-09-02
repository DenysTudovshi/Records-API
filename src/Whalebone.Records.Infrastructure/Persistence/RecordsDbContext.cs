using Microsoft.EntityFrameworkCore;

using Whalebone.Records.Application.Domain;

namespace Whalebone.Records.Infrastructure.Persistence;

public sealed class RecordsDbContext(DbContextOptions<RecordsDbContext> options) : DbContext(options)
{
    public DbSet<PersonRecord> Records => Set<PersonRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RecordsDbContext).Assembly);
    }
}
