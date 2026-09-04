namespace Whalebone.Records.IntegrationTests.Infrastructure;

[Collection(PostgresCollection.Name)]
public abstract class IntegrationTestBase(PostgresFixture fixture) : IAsyncLifetime
{
    protected HttpClient Client => fixture.Client;

    /// <summary>Every log line the host has written.</summary>
    protected CapturingLoggerProvider Logs => fixture.Logs;

    /// <summary>The running host's services, for assertions about how it was configured.</summary>
    protected IServiceProvider Services => fixture.Services;

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The <c>errors[]</c> entries of an error body, keyed by the <c>parameter</c> each one names.
    /// </summary>
    /// <remarks>
    /// A structural query, not test logic: the assertions stay in the tests, where a reader can see
    /// them. Throws if the body has no <c>errors</c> member, which is itself the right failure - a
    /// test asking for a parameter's error wants to know when the array was never written.
    /// </remarks>
    protected static async Task<Dictionary<string, JsonElement>> ErrorsByParameterAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        return body.GetProperty("errors").EnumerateArray().ToDictionary(
            entry => entry.GetProperty("parameter").GetString()!,
            entry => entry,
            StringComparer.Ordinal);
    }

    /// <summary>Builds a request body as raw JSON, so the tests exercise the wire contract rather than a C# type.</summary>
    protected static StringContent Body(
        string externalId,
        string name = "some name",
        string email = "email@email.com",
        string dateOfBirth = "2020-01-01T12:12:34+00:00")
    {
        var json = $$"""
            {"external_id":"{{externalId}}","name":"{{name}}","email":"{{email}}","date_of_birth":"{{dateOfBirth}}"}
            """;

        return new StringContent(json, Encoding.UTF8, "application/json");
    }
}
