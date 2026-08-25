using System.Xml.Linq;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace AI.Investment.Infrastructure.Persistence;

/// <summary>
/// Lets the EF tooling construct the context without starting the application.
/// </summary>
/// <remarks>
/// <para>
/// EF Core's design-time tooling has a precedence rule worth knowing: <strong>if a class
/// implementing <see cref="IDesignTimeDbContextFactory{TContext}"/> exists in the DbContext
/// project or the startup project, the tools bypass every other way of creating the context and
/// use the factory.</strong> The application host is never built, so <c>Program.cs</c>,
/// <c>AddInfrastructure</c> and everything they configure are not consulted.
/// </para>
/// <para>
/// That is the point - adding a migration should not require a working deployment - but it has a
/// sharp edge. An earlier version of this class carried a hard-coded connection string as a
/// fallback, described as being used "only to determine the SQL dialect". That was true of
/// <c>migrations add</c>, which merely scaffolds, and false of <c>database update</c>, which
/// genuinely connects. The result was that schema could be applied to whatever that constant
/// happened to name rather than to the configured database. There is now no fallback: this class
/// reads the same configuration the application reads, and refuses to construct a context if it
/// cannot find a connection string.
/// </para>
/// <para>
/// Resolution order, highest priority first:
/// </para>
/// <list type="number">
/// <item><c>AIINV_DESIGNTIME_DB</c> - an explicit override, for pointing the tooling at a
/// scratch database without touching any file.</item>
/// <item>Environment variables, so <c>Database__ConnectionString</c> works as it does at
/// runtime.</item>
/// <item>User secrets for the API project, which is where a real connection string belongs.</item>
/// <item><c>appsettings.{Environment}.json</c> in the API project.</item>
/// <item><c>appsettings.json</c> in the API project.</item>
/// </list>
/// <para>
/// The environment defaults to <c>Development</c> rather than <c>Production</c>. That is
/// deliberate and it is the safer direction: running <c>database update</c> with nothing set
/// targets the development database, and reaching a production database requires deliberately
/// setting <c>ASPNETCORE_ENVIRONMENT</c>. A default that could silently migrate production would
/// be the wrong kind of convenience.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>Explicit override, consulted before any configuration file.</summary>
    public const string ConnectionStringEnvironmentVariable = "AIINV_DESIGNTIME_DB";

    private const string SolutionFileName = "AI-Investment-Analyst.sln";
    private const string ApiProjectDirectoryName = "AI.Investment.Api";
    private const string ApiProjectFileName = "AI.Investment.Api.csproj";
    private const string DefaultEnvironmentName = "Development";

    public AppDbContext CreateDbContext(string[] args)
    {
        var apiProjectDirectory = LocateApiProjectDirectory();
        var environmentName = ResolveEnvironmentName();
        var configuration = BuildConfiguration(apiProjectDirectory, environmentName);
        var connectionString = ResolveConnectionString(configuration, apiProjectDirectory, environmentName);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(
                typeof(DesignTimeDbContextFactory).Assembly.FullName))
            .Options;

        // The EF tooling never calls SaveChanges, so the write guard is never consulted. This
        // stub exists because AppDbContext requires the dependency in order to construct, and it
        // throws rather than returning a permissive value - design time must not be a way to
        // obtain write authorisation.
        return new AppDbContext(options, new DesignTimeWriteAuthorization());
    }

    /// <summary>
    /// Finds the API project directory, which owns the configuration files.
    /// </summary>
    /// <remarks>
    /// Located by walking up from the current directory to the solution file, rather than by
    /// trusting the tooling's working directory - which differs between <c>dotnet ef</c>, the
    /// Package Manager Console and an IDE. Anchoring on the solution makes the result the same
    /// however the tooling was invoked.
    /// </remarks>
    private static string LocateApiProjectDirectory()
    {
        var candidates = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };

        foreach (var start in candidates)
        {
            // The tooling may already be running in the API project directory.
            if (File.Exists(Path.Combine(start, ApiProjectFileName)))
            {
                return start;
            }

            var directory = new DirectoryInfo(start);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                {
                    var apiDirectory = Path.Combine(directory.FullName, "src", ApiProjectDirectoryName);

                    if (File.Exists(Path.Combine(apiDirectory, ApiProjectFileName)))
                    {
                        return apiDirectory;
                    }
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate the '{ApiProjectDirectoryName}' project by walking up from " +
            $"'{Directory.GetCurrentDirectory()}' looking for '{SolutionFileName}'. Run the EF " +
            "tooling from inside the repository, or set " +
            $"{ConnectionStringEnvironmentVariable} to supply the connection string directly.");
    }

    private static string ResolveEnvironmentName() =>
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? DefaultEnvironmentName;

    private static IConfigurationRoot BuildConfiguration(string apiProjectDirectory, string environmentName)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(apiProjectDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false);

        // User secrets are where a real connection string belongs, so the design-time tooling has
        // to read them or it would be resolving a different value than the running application.
        var userSecretsId = ReadUserSecretsId(apiProjectDirectory);

        if (!string.IsNullOrWhiteSpace(userSecretsId))
        {
            builder.AddUserSecrets(userSecretsId);
        }

        // Last, so an environment variable wins over a file - matching the application's own
        // precedence, and allowing Database__ConnectionString to work here as it does at runtime.
        builder.AddEnvironmentVariables();

        return builder.Build();
    }

    /// <summary>
    /// Reads <c>UserSecretsId</c> from the API project file.
    /// </summary>
    /// <remarks>
    /// Parsed from the .csproj rather than duplicated as a constant here. The identifier is not a
    /// secret, but two copies of it would drift, and a design-time factory silently reading a
    /// different secret store than the application is exactly the class of bug this whole change
    /// exists to remove. Infrastructure cannot reference the API assembly to read its
    /// <c>UserSecretsIdAttribute</c> - that would invert the dependency - so the project file is
    /// the single source of truth available.
    /// </remarks>
    private static string? ReadUserSecretsId(string apiProjectDirectory)
    {
        var projectFile = Path.Combine(apiProjectDirectory, ApiProjectFileName);

        if (!File.Exists(projectFile))
        {
            return null;
        }

        try
        {
            return XDocument.Load(projectFile)
                .Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "UserSecretsId", StringComparison.Ordinal))
                ?.Value
                .Trim();
        }
        catch (System.Xml.XmlException)
        {
            // A malformed project file is not this class's problem to report - the build will say
            // so far more clearly. Continue without user secrets.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the connection string, or throws with instructions. There is no fallback.
    /// </summary>
    private static string ResolveConnectionString(
        IConfiguration configuration,
        string apiProjectDirectory,
        string environmentName)
    {
        var explicitOverride = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(explicitOverride))
        {
            return explicitOverride;
        }

        var fromConfiguration = configuration[$"{DatabaseOptions.SectionName}:ConnectionString"];

        if (!string.IsNullOrWhiteSpace(fromConfiguration))
        {
            return fromConfiguration;
        }

        throw new InvalidOperationException(
            $"No database connection string is configured for the '{environmentName}' environment, " +
            "so the EF tooling has nothing to connect to. This is reported rather than defaulted: " +
            "a hard-coded fallback here would silently apply migrations to a database nobody chose." +
            Environment.NewLine + Environment.NewLine +
            $"Configuration was read from '{apiProjectDirectory}'." +
            Environment.NewLine +
            $"Supply '{DatabaseOptions.SectionName}:ConnectionString' by any of:" +
            Environment.NewLine +
            $"  1. set {ConnectionStringEnvironmentVariable}=<connection string>   (explicit override)" +
            Environment.NewLine +
            $"  2. set {DatabaseOptions.SectionName}__ConnectionString=<connection string>" +
            Environment.NewLine +
            "  3. dotnet user-secrets set \"Database:ConnectionString\" \"<connection string>\" " +
            "--project src/AI.Investment.Api" +
            Environment.NewLine +
            $"  4. the '{DatabaseOptions.SectionName}:ConnectionString' entry in " +
            $"appsettings.{environmentName}.json" +
            Environment.NewLine + Environment.NewLine +
            "The environment defaults to Development; set ASPNETCORE_ENVIRONMENT to target another.");
    }

    private sealed class DesignTimeWriteAuthorization : IWriteAuthorization
    {
        public bool IsAuthorized => false;

        public Guid? AuthorizingDecisionId => null;

        public IDisposable Authorize(PolicyDecision decision) =>
            throw new NotSupportedException(
                "Write authorisation is not available at design time. The EF tooling does not " +
                "execute application code paths.");
    }
}
