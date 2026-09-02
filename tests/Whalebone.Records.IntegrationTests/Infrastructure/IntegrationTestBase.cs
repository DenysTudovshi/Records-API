namespace Whalebone.Records.IntegrationTests.Infrastructure;

[Collection(PostgresCollection.Name)]
public abstract class IntegrationTestBase(PostgresFixture fixture) : IAsyncLifetime
{
    protected HttpClient Client => fixture.Client;

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

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
