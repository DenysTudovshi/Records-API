using Whalebone.Records.Api.Endpoints.Records;

namespace Whalebone.Records.Api.Endpoints;

/// <summary>
/// Registers every endpoint. Resolved through <c>static abstract</c> rather than assembly
/// scanning, so a missing registration is a compile error and startup stays reflection-free.
/// </summary>
internal static class EndpointRegistration
{
    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var records = app.MapGroup(string.Empty).WithTags("Records");

        Map<SaveRecord>(records);
        Map<GetRecordById>(records);

        return app;
    }

    private static void Map<TEndpoint>(IEndpointRouteBuilder builder)
        where TEndpoint : IEndpoint =>
        TEndpoint.Map(builder);
}
