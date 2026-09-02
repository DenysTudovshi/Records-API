namespace Whalebone.Records.Api;

/// <summary>Every route the service serves, in one place.</summary>
internal static class ApiRoutes
{
    internal const string Save = "/save";

    /// <summary>
    /// The <c>:guid</c> constraint means a non-UUID segment fails at routing rather than
    /// reaching a handler, and keeps this from shadowing <c>/health</c> or <c>/swagger</c>.
    /// </summary>
    internal const string GetById = "/{id:guid}";

    internal const string HealthLive = "/health/live";

    internal const string HealthReady = "/health/ready";
}
