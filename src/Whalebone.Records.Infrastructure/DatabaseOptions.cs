using System.ComponentModel.DataAnnotations;

namespace Whalebone.Records.Infrastructure;

/// <summary>
/// Database configuration, bound from the <c>Database</c> section. In a container this
/// arrives as <c>Database__ConnectionString</c>; locally it can come from appsettings,
/// user secrets, or a <c>--Database:ConnectionString=</c> command-line argument.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Validated at startup, so a missing value fails fast and loudly instead of at first request.</summary>
    /// <remarks>
    /// The message names the setting but deliberately carries no example connection
    /// string. Validation messages are written to stderr and to the log sink, and a
    /// credential-shaped literal has no business in either - not even a placeholder one,
    /// which is exactly the sort of line that later gets edited to a real value.
    /// </remarks>
    [Required(AllowEmptyStrings = false, ErrorMessage =
        "Database__ConnectionString is required and must not be empty. " +
        "See compose.yaml or the Configuration section of the README.")]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Applies pending migrations during startup. On by default so a cold start just works.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>Transient-failure retries, which matter while Postgres is still accepting connections.</summary>
    [Range(0, 20)]
    public int MaxRetryCount { get; set; } = 8;

    [Range(1, 300)]
    public int MigrationTimeoutSeconds { get; set; } = 60;
}
