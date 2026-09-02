using Microsoft.EntityFrameworkCore;

using Npgsql;

using Whalebone.Records.Application.Abstractions;
using Whalebone.Records.Application.Domain;

namespace Whalebone.Records.Infrastructure.Persistence;

internal sealed class RecordRepository(RecordsDbContext dbContext) : IRecordRepository
{
    /// <summary>PostgreSQL <c>unique_violation</c>.</summary>
    private const string UniqueViolation = "23505";

    public Task<PersonRecord?> GetByExternalIdAsync(Guid externalId, CancellationToken cancellationToken) =>
        dbContext.Records.SingleOrDefaultAsync(record => record.ExternalId == externalId, cancellationToken);

    public void Add(PersonRecord record) => dbContext.Records.Add(record);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Detach whatever we failed to insert. Without this the change tracker would
            // replay the same doomed INSERT on the caller's next SaveChanges, and the
            // converge-on-update retry could never succeed.
            foreach (var entry in dbContext.ChangeTracker.Entries<PersonRecord>()
                         .Where(entry => entry.State == EntityState.Added)
                         .ToArray())
            {
                entry.State = EntityState.Detached;
            }

            var externalId = exception.Entries
                .Select(entry => entry.Entity)
                .OfType<PersonRecord>()
                .Select(record => record.ExternalId)
                .FirstOrDefault();

            throw new DuplicateExternalIdException(externalId, exception);
        }
    }
}
