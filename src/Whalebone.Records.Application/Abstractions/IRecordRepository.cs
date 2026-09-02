using Whalebone.Records.Application.Domain;

namespace Whalebone.Records.Application.Abstractions;

/// <summary>
/// Persistence for <see cref="PersonRecord"/>. Declared here and implemented in
/// Infrastructure so the dependency arrow points inward, at the use cases.
/// </summary>
public interface IRecordRepository
{
    Task<PersonRecord?> GetByExternalIdAsync(Guid externalId, CancellationToken cancellationToken);

    void Add(PersonRecord record);

    /// <summary>Flushes pending changes.</summary>
    /// <exception cref="DuplicateExternalIdException">
    /// A competing writer inserted the same <c>external_id</c> first. Implementations
    /// must leave the unit of work usable, so the caller can converge on an update.
    /// </exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
