namespace Whalebone.Records.Api.Endpoints;

/// <summary>One HTTP endpoint: its route, its OpenAPI metadata and its handler, together.</summary>
public interface IEndpoint
{
    /// <summary>Maps this endpoint onto <paramref name="app"/>.</summary>
    static abstract void Map(IEndpointRouteBuilder app);
}
