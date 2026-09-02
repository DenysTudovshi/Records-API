namespace Whalebone.Records.Application.Abstractions;

/// <summary>
/// A unique-constraint violation on <c>external_id</c>, translated out of the database
/// provider so the use cases can react to it without referencing Npgsql or EF Core.
/// </summary>
public sealed class DuplicateExternalIdException : Exception
{
    public DuplicateExternalIdException(Guid externalId, Exception innerException)
        : base($"A record with external_id '{externalId}' already exists.", innerException) =>
        ExternalId = externalId;

    public DuplicateExternalIdException()
    {
    }

    public DuplicateExternalIdException(string message)
        : base(message)
    {
    }

    public DuplicateExternalIdException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public Guid ExternalId { get; }
}
