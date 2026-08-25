using AI.Investment.Application.Abstractions;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Auditing;
using AI.Investment.Infrastructure.Configuration;
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
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IDatabaseConnectivityProbe, DatabaseConnectivityProbe>();
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
