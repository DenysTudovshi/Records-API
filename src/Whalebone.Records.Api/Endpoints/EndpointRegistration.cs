using Whalebone.Records.Api.Endpoints.Records;

namespace Whalebone.Records.Api.Endpoints;

/// <summary>
/// Registers every endpoint. Resolved through <c>static abstract</c> rather than assembly
/// scanning, so a missing registration is a compile error rather than a missing route, and
/// mapping costs no reflection. Startup as a whole is not reflection-free - MediatR and
/// FluentValidation each scan the Application assembly, which the README prices.
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
