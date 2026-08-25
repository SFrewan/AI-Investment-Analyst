using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Auditing;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using AI.Investment.Infrastructure.Policy;
using AI.Investment.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AI.Investment.Infrastructure;

/// <summary>Registers persistence, safety and time services.</summary>
/// <remarks>
/// Called from the API's composition root. This is the only point at which the API project is
/// permitted to touch an Infrastructure type; an architecture test enforces that.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddOptions(services, configuration);
        AddPersistence(services, configuration);
        AddSafety(services);
        AddIngestion(services, configuration);

        services.AddSingleton<IClock, SystemClock>();

        return services;
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration)
    {
        // Bound and validated here, because the shape of this configuration is Infrastructure's
        // own business. ValidateOnStart is deliberately NOT called here: it lives in
        // Microsoft.Extensions.Hosting, and deciding that a bad value should stop the HOST from
        // starting is a hosting decision, not a persistence one. A class library that reaches
        // into the host lifecycle has confused the two. The API's composition root opts in - see
        // Program.ConfigureServices.
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations();

        // Deliberately NOT ValidateOnStart. Safety configuration is read through
        // IOptionsMonitor at evaluation time so that a policy change takes effect without a
        // restart, and the provider fails closed if it cannot read it. Refusing to start on a
        // malformed policy would be the wrong failure: the system should come up and deny,
        // where an operator can see it, rather than not come up at all.
        services.AddOptions<SafetyOptions>()
            .Bind(configuration.GetSection(SafetyOptions.SectionName));

        // Same reasoning as SafetyOptions: a misconfigured connector should leave the platform
        // running with that connector absent - which the ingestion gateway reports as a named
        // refusal in the ledger - rather than prevent the host from starting at all.
        services.AddOptions<SecEdgarOptions>()
            .Bind(configuration.GetSection(SecEdgarOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddOptions<RawArchiveOptions>()
            .Bind(configuration.GetSection(RawArchiveOptions.SectionName))
            .ValidateDataAnnotations();
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration[$"{DatabaseOptions.SectionName}:ConnectionString"];

        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var databaseOptions = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>()
                .Value;

            options.UseNpgsql(
                string.IsNullOrWhiteSpace(databaseOptions.ConnectionString)
                    ? connectionString
                    : databaseOptions.ConnectionString,
                npgsql =>
                {
                    npgsql.CommandTimeout(databaseOptions.CommandTimeoutSeconds);
                    npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                });

            if (databaseOptions.EnableSensitiveDataLogging)
            {
                // Development only. These values are the contents of the database.
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
        });

        services.AddScoped<ICompanyRepository, CompanyRepository>();
        services.AddScoped<ISourceRegistry, EfSourceRegistry>();
        services.AddScoped<IIngestionRunStore, EfIngestionRunStore>();
        services.AddScoped<IUnreplayableEvidenceStore, EfUnreplayableEvidenceStore>();
        services.AddScoped<IPayloadReferenceIndex, EfPayloadReferenceIndex>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IDatabaseConnectivityProbe, DatabaseConnectivityProbe>();
    }

    /// <summary>
    /// Registers the data plane: the rate limiter, the connector catalogue, the ingestion
    /// gateway, and whichever connectors this installation has configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A connector is registered only when its configuration is complete. An installation that has
    /// not supplied a contact address for EDGAR gets no EDGAR connector, and the gateway refuses
    /// runs for that source with <c>ingestion.provider-available@1</c> - recorded, explained, and
    /// safe. The alternative, registering it with a placeholder identity, would violate the SEC's
    /// fair-access policy on the first request.
    /// </para>
    /// <para>
    /// Adding a provider later is one registration here and nothing else. Nothing above this line
    /// enumerates connectors, which is what lets market data, fundamentals, news, macroeconomic
    /// series and future opportunity domains arrive without touching the architecture.
    /// </para>
    /// </remarks>
    private static void AddIngestion(IServiceCollection services, IConfiguration configuration)
    {
        // Singleton: the sliding window is the state that makes the quota mean anything, and a
        // per-request limiter would permit the full quota per request.
        services.AddSingleton<IProviderRateLimiter, SlidingWindowRateLimiter>();

        // Scoped, matching the connectors it composes - a typed HttpClient is transient by design
        // so that its handler can rotate, and a singleton catalogue would capture one for the life
        // of the process.
        services.AddScoped<IProviderCatalogue, ProviderCatalogue>();

        // Singleton: the archive holds no per-request state, and its only field is a resolved
        // root path. Its idempotence comes from content addressing rather than from coordination.
        services.AddSingleton<IRawResponseArchive, FileSystemRawResponseArchive>();

        // IIngestionGateway and IRetentionEnforcer are Application services and are registered in
        // AddApplication, alongside IActionGateway. What made them wait was this layer: until the
        // persistence stage there was no ISourceRegistry, IIngestionRunStore, IRawResponseArchive,
        // IPayloadReferenceIndex or IUnreplayableEvidenceStore to construct them from, and
        // ASP.NET Core validates the container on build in Development - so an unconstructable
        // registration would have failed the whole host's start-up rather than leaving one feature
        // absent. All five now exist.
        AddSecEdgar(services, configuration);
    }

    private static void AddSecEdgar(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SecEdgarOptions.SectionName);

        if (!bool.TryParse(section["Enabled"], out var enabled) || !enabled)
        {
            return;
        }

        // Read directly rather than through the options binder: this decision is made while
        // building the container, before any options instance can be resolved.
        var baseAddress = section["BaseAddress"];

        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            baseAddress = new SecEdgarOptions().BaseAddress;
        }

        services
            .AddHttpClient<SecEdgarProvider>(client =>
            {
                client.BaseAddress = new Uri(baseAddress, UriKind.Absolute);

                // A request that has not answered in half a minute is a request the scheduler
                // should be told about, not one a thread should keep waiting on.
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddTransient<IDataProvider>(provider => provider.GetRequiredService<SecEdgarProvider>());
    }

    private static void AddSafety(IServiceCollection services)
    {
        // Scoped: an authorisation window belongs to one request or one operating cycle and must
        // not leak across them.
        services.AddScoped<IWriteAuthorization, ScopedWriteAuthorization>();

        services.AddScoped<IPolicyContextProvider, ConfiguredPolicyContextProvider>();
        services.AddScoped<IAuditSink, EfAuditSink>();
        services.AddScoped<IActionExecutionStore, EfActionExecutionStore>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
    }
}
