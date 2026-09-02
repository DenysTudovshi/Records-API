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
    [Required(AllowEmptyStrings = false, ErrorMessage =
        "Database__ConnectionString is required. Example: " +
        "Host=db;Port=5432;Database=whalebone;Username=whalebone;Password=whalebone")]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Applies pending migrations during startup. On by default so a cold start just works.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>Transient-failure retries, which matter while Postgres is still accepting connections.</summary>
    [Range(0, 20)]
    public int MaxRetryCount { get; set; } = 8;

    [Range(1, 300)]
    public int MigrationTimeoutSeconds { get; set; } = 60;
}
