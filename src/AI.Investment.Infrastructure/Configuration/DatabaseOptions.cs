using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>PostgreSQL connection settings.</summary>
/// <remarks>
/// The connection string is a secret and belongs in the .NET user-secrets store in development
/// and in an environment variable or managed secret store in production - never in
/// appsettings.json. See docs/SECURITY.md.
/// </remarks>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>Seconds before a command times out.</summary>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Whether EF may include parameter values in exception and log messages. Development only:
    /// those values are the contents of the database.
    /// </summary>
    public bool EnableSensitiveDataLogging { get; init; }
}
