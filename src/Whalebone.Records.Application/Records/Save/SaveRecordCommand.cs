using MediatR;

using Whalebone.Records.Application.Abstractions;
using Whalebone.Records.Application.Domain;

namespace Whalebone.Records.Application.Records.Save;

/// <remarks>
/// Every member is nullable because the wire can omit any of them, and "absent" is a different
/// answer to the caller than "present but unusable" - the difference between the two error codes
/// this contract reports. Collapsing them onto defaults here would throw that away before the
/// validator ever saw it. <see cref="Behaviors.ValidationBehavior{TRequest,TResponse}"/> runs
/// ahead of every handler, so a handler that is reached at all has all four.
/// </remarks>
public sealed record SaveRecordCommand(
    Guid? ExternalId,
    string? Name,
    string? Email,
    DateTimeOffset? DateOfBirth) : IRequest<SaveRecordResult>;

/// <param name="Created">
/// True when this call inserted the record, false when it updated an existing one.
/// Drives 201 vs 200 at the edge.
/// </param>
public sealed record SaveRecordResult(PersonRecordDto Record, bool Created);

/// <remarks>
/// <c>POST /save</c> is treated as an upsert keyed on <c>external_id</c> - the verb is
/// <c>save</c>, not <c>create</c>. The caller supplies the identity, so the operation is
/// idempotent (201 on create, 200 on replace) and a retry after a network blip converges
/// instead of failing. <c>409 Conflict</c> with create-only semantics would be a smaller
/// change and equally defensible, but not idempotent: a retried request that had already
/// succeeded would then report failure.
/// </remarks>
internal sealed class SaveRecordCommandHandler(
    IRecordRepository repository,
    TimeProvider timeProvider) : IRequestHandler<SaveRecordCommand, SaveRecordResult>
{
    public async Task<SaveRecordResult> Handle(SaveRecordCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The null-forgiving operators below are load-bearing on ValidationBehavior, which runs
        // ahead of every handler: a command that reaches here has already been proved complete.
        var existing = await repository
            .GetByExternalIdAsync(request.ExternalId!.Value, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var created = PersonRecord.Create(
                request.ExternalId!.Value,
                request.Name!,
                request.Email!,
                request.DateOfBirth!.Value,
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
                    .GetByExternalIdAsync(request.ExternalId!.Value, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is null)
                {
                    throw;
                }
            }
        }

        existing.Update(request.Name!, request.Email!, request.DateOfBirth!.Value, timeProvider.GetUtcNow());
        await repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new SaveRecordResult(PersonRecordDto.From(existing), Created: false);
    }
}
