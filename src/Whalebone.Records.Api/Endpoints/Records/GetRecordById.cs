using MediatR;

using Whalebone.Records.Application.Records;
using Whalebone.Records.Application.Records.GetById;

namespace Whalebone.Records.Api.Endpoints.Records;

/// <summary>Reads a record by its <c>external_id</c>.</summary>
internal sealed class GetRecordById : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(ApiRoutes.GetById, HandleAsync)
            .WithName(nameof(GetRecordById))
            .WithSummary("Reads a record by external_id.")
            .Produces<PersonRecordDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleAsync(
        Guid id,
        ISender sender,
        CancellationToken cancellationToken)
    {
        var record = await sender.Send(new GetRecordQuery(id), cancellationToken).ConfigureAwait(false);

        return record is null
            ? Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Record not found.",
                detail: $"No record exists with external_id '{id}'.")
            : Results.Ok(record);
    }
}
