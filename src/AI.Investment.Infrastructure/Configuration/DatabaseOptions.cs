using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>PostgreSQL connection settings.</summary>
/// <remarks>
/// The connection string is a secret and belongs in the .NET user-secrets store in development
/// and in an environment variable or managed secret store in production - never in
/// appsettings.json. See docs/SECURITY.md and docs/LOCAL-DEVELOPMENT.md.
/// </remarks>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// The message shown when no connection string has been supplied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The validation itself is unchanged: the field is still required and an empty string is
    /// still a failure, in every environment. What changes is what the failure says.
    /// </para>
    /// <para>
    /// <see cref="Persistence.DesignTimeDbContextFactory"/> already answers this question well -
    /// it names the key, lists the three mechanisms in precedence order, and prints the exact
    /// command. A developer who hit the runtime path instead got "The ConnectionString field is
    /// required", which is true and tells them nothing about where to put one. The two failures
    /// now say the same thing.
    /// </para>
    /// </remarks>
    public const string MissingConnectionStringMessage =
        "No PostgreSQL connection string is configured. Supply 'Database:ConnectionString' by " +
        "one of: (1) dotnet user-secrets set \"Database:ConnectionString\" \"...\" - run from " +
        "src/AI.Investment.Api, and the preferred mechanism in development; (2) the environment " +
        "variable Database__ConnectionString; (3) a managed secret store in production. It must " +
        "not be put in appsettings.json, which is committed. See docs/LOCAL-DEVELOPMENT.md.";

    [Required(AllowEmptyStrings = false, ErrorMessage = MissingConnectionStringMessage)]
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
