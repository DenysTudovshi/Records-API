using MediatR;

using Whalebone.Records.Application.Abstractions;

namespace Whalebone.Records.Application.Records.GetById;

/// <summary>Reads a record by its caller-supplied <c>external_id</c>.</summary>
public sealed record GetRecordQuery(Guid ExternalId) : IRequest<PersonRecordDto?>;

internal sealed class GetRecordQueryHandler(IRecordRepository repository)
    : IRequestHandler<GetRecordQuery, PersonRecordDto?>
{
    public async Task<PersonRecordDto?> Handle(GetRecordQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var record = await repository
            .GetByExternalIdAsync(request.ExternalId, cancellationToken)
            .ConfigureAwait(false);

        return record is null ? null : PersonRecordDto.From(record);
    }
}
