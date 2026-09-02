using MediatR;

using Whalebone.Records.Api.Contracts;
using Whalebone.Records.Application.Records;

namespace Whalebone.Records.Api.Endpoints.Records;

/// <summary>Stores a record, keyed on the caller-supplied <c>external_id</c>.</summary>
internal sealed class SaveRecord : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(ApiRoutes.Save, HandleAsync)
            .WithName(nameof(SaveRecord))
            .WithSummary("Stores a record.")
            .WithDescription(
                "Idempotent on external_id: a new record is created (201) and an existing " +
                "one is replaced (200). The Location header points at GET /{external_id}.")
            .Produces<PersonRecordDto>(StatusCodes.Status201Created)
            .Produces<PersonRecordDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem();
    }

    private static async Task<IResult> HandleAsync(
        SaveRecordRequest request,
        ISender sender,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(request.ToCommand(), cancellationToken).ConfigureAwait(false);

        return result.Created
            ? Results.Created($"/{result.Record.ExternalId}", result.Record)
            : Results.Ok(result.Record);
    }
}
