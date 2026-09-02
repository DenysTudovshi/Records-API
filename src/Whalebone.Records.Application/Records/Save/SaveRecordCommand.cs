using MediatR;

using Whalebone.Records.Application.Abstractions;
using Whalebone.Records.Application.Domain;

namespace Whalebone.Records.Application.Records.Save;

public sealed record SaveRecordCommand(
    Guid ExternalId,
    string Name,
    string Email,
    DateTimeOffset DateOfBirth) : IRequest<SaveRecordResult>;

/// <param name="Created">
/// True when this call inserted the record, false when it updated an existing one.
/// Drives 201 vs 200 at the edge.
/// </param>
public sealed record SaveRecordResult(PersonRecordDto Record, bool Created);

/// <remarks>
/// <c>POST /save</c> is treated as an upsert keyed on <c>external_id</c>: the caller
/// supplies the identity, so the operation is idempotent and a retry after a network
/// blip converges instead of failing.
/// </remarks>
internal sealed class SaveRecordCommandHandler(
    IRecordRepository repository,
    TimeProvider timeProvider) : IRequestHandler<SaveRecordCommand, SaveRecordResult>
{
    public async Task<SaveRecordResult> Handle(SaveRecordCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = await repository
            .GetByExternalIdAsync(request.ExternalId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var created = PersonRecord.Create(
                request.ExternalId,
                request.Name,
                request.Email,
                request.DateOfBirth,
                timeProvider.GetUtcNow());

            repository.Add(created);

            try
            {
                await repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new SaveRecordResult(PersonRecordDto.From(created), Created: true);
            }
            catch (DuplicateExternalIdException)
            {
                // A concurrent request inserted the same external_id between our read and
                // our write. The row exists now, so converge on the update path. One retry,
                // no loop: the second attempt cannot hit the same race, because the losing
                // insert has been detached and we re-read the winner.
                existing = await repository
                    .GetByExternalIdAsync(request.ExternalId, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is null)
                {
                    throw;
                }
            }
        }

        existing.Update(request.Name, request.Email, request.DateOfBirth, timeProvider.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new SaveRecordResult(PersonRecordDto.From(existing), Created: false);
    }
}
