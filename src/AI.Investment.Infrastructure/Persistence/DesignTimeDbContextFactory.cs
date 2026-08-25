using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AI.Investment.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> construct the context without starting the application.
/// </summary>
/// <remarks>
/// <para>
/// Without this, <c>dotnet ef migrations add</c> boots the API host to find the context, which
/// means design-time tooling depends on a valid runtime configuration - including a real
/// connection string and passing options validation. Adding a migration should not require a
/// working deployment.
/// </para>
/// <para>
/// The connection string here is used only to determine the provider's SQL dialect while
/// scaffolding; no connection is opened. Set <c>AIINV_DESIGNTIME_DB</c> to override it.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public const string ConnectionStringEnvironmentVariable = "AIINV_DESIGNTIME_DB";

    private const string FallbackConnectionString =
        "Host=localhost;Port=5432;Database=ai_investment;Username=postgres;Password=postgres";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
            ?? FallbackConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(
                typeof(DesignTimeDbContextFactory).Assembly.FullName))
            .Options;

        // Design-time only. The scaffolder never calls SaveChanges, so this stub is never
        // consulted; it exists because the context requires the dependency to construct.
        return new AppDbContext(options, new DesignTimeWriteAuthorization());
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
