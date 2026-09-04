using MediatR;

using Whalebone.Records.Api.Contracts;
using Whalebone.Records.Application.Records;
using Whalebone.Records.Application.Records.GetById;

namespace Whalebone.Records.Api.Endpoints.Records;

/// <summary>Reads a record by its <c>external_id</c>.</summary>
/// <remarks>
/// <c>{id}</c> is the <c>external_id</c>, not the surrogate key: it is the only identifier the
/// contract defines, and the one a client already holds because it supplied it. Resolving against
/// a server-generated id would force a POST-and-parse before anything could be read back, and add
/// a fifth response field the brief does not define.
/// </remarks>
internal sealed class GetRecordById : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(ApiRoutes.GetById, HandleAsync)
            .WithName(nameof(GetRecordById))
            .WithSummary("Reads a record by external_id.")
            .Produces<PersonRecordDto>(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleAsync(
        Guid id,
        ISender sender,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var record = await sender.Send(new GetRecordQuery(id), cancellationToken).ConfigureAwait(false);

        // No errors[] entry: nothing about the request was wrong, the row simply is not there.
        return record is null
            ? Results.Json(
                ErrorResponse.Plain(
                    $"No record exists with external_id '{id}'.",
                    httpContext.TraceIdentifier),
                statusCode: StatusCodes.Status404NotFound,
                contentType: "application/json")
            : Results.Ok(record);
    }
}
