using AI.Investment.Api.Configuration;
using AI.Investment.Api.Correlation;
using AI.Investment.Api.Diagnostics;
using AI.Investment.Api.HostedServices;
using AI.Investment.Api.Middleware;
using AI.Investment.Application;
using AI.Investment.Application.Abstractions;
using AI.Investment.Infrastructure;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Events;

namespace AI.Investment.Api;

/// <summary>
/// Composition root for the AI Investment Analyst API.
/// </summary>
/// <remarks>
/// <para>
/// This is the ONLY file in this project permitted to reference types from
/// AI.Investment.Infrastructure. The API references that project so the DI container can be
/// wired here; everywhere else the API talks to the Application layer's abstractions. An
/// architecture test enforces this rather than leaving it to discipline.
/// </para>
/// <para>
/// Written as an explicit class rather than top-level statements: top-level statements place
/// the generated <c>Program</c> type in the global namespace, which trips CA1050 under
/// warnings-as-errors and needs a partial-class workaround for
/// <c>WebApplicationFactory&lt;Program&gt;</c>. An ordinary class avoids both.
/// </para>
/// </remarks>
public sealed class Program
{
    // Allocated once rather than on every call (CA1861). Health-check registration happens at
    // start-up only, so this is about the rule being consistently applied rather than about
    // this particular allocation.
    private static readonly string[] LivenessTags = ["live"];
    private static readonly string[] ReadinessTags = ["ready"];

    // Sealed with a private constructor rather than 'static': WebApplicationFactory<Program>
    // in AI.Investment.Api.Tests needs Program as a generic type argument, and a static class
    // cannot be one (CS0718).
    private Program()
    {
    }

    public static async Task<int> Main(string[] args)
    {
        // A bootstrap logger, so that failures during host construction - a missing
        // connection string, a failed options validation - are recorded rather than lost
        // to a blank console.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            Log.Information("Starting AI Investment Analyst API host");

            var app = BuildApplication(args);
            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
#pragma warning disable CA1031 // Deliberate: the top-level handler must record ANY start-up
                              // failure before the process exits, or the reason is lost.
        catch (Exception ex)
        {
            Log.Fatal(ex, "AI Investment Analyst API host terminated unexpectedly");
            return 1;
        }
#pragma warning restore CA1031
        finally
        {
            // Async variant, because this sits inside an async method and the sync call is a
            // sync-over-async block (CA1849). Flushing matters here: buffered log events for
            // the failure that is terminating the process are exactly the ones worth keeping.
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the configured application. Separated from <see cref="Main"/> so that
    /// integration tests can exercise the same composition without starting a process.
    /// </summary>
    internal static WebApplication BuildApplication(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        ConfigureLogging(builder);
        ConfigureServices(builder);

        var app = builder.Build();
        ConfigurePipeline(app);

        return app;
    }

    /// <summary>
    /// Configures the host's logger from configuration and DI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>preserveStaticLogger: true</c> is load-bearing, not decoration.</strong> The
    /// bootstrap logger in <see cref="Main"/> is a Serilog <c>ReloadableLogger</c> held in the
    /// static <c>Log.Logger</c>. With the default <c>preserveStaticLogger: false</c>, building the
    /// host <em>freezes</em> that reloadable logger - and a frozen logger cannot be frozen again.
    /// </para>
    /// <para>
    /// One host per process makes that invisible. A test process does not: every
    /// <c>WebApplicationFactory</c> fixture builds its own host, so the second build threw
    /// <c>InvalidOperationException: The logger is already frozen</c>, the entry point exited
    /// without producing a host, and every API test failed with "The entry point exited without
    /// ever building an IHost". Three test classes, three fixtures, three failures.
    /// </para>
    /// <para>
    /// Preserving the static logger separates the two paths honestly rather than papering over the
    /// collision. The host's <c>ILogger&lt;T&gt;</c> - which is what controllers, hosted services
    /// and the framework use - is still built from this delegate, with configuration, enrichment
    /// and DI. The static <c>Log</c> stays the bootstrap console logger, which is all
    /// <see cref="Main"/> needs it for: the two messages it writes are a start-up line and a fatal
    /// exception, both of which must work <em>before</em> a host exists, and both of which go to
    /// the console either way because that is this application's only sink.
    /// </para>
    /// <para>
    /// The alternative - a fresh bootstrap logger per build - would also compile, and would race:
    /// xUnit runs test collections in parallel, so two fixtures would be assigning and freezing the
    /// same process-wide static at once. Removing the shared mutable static from the host-build
    /// path is the fix; making it churn faster is not.
    /// </para>
    /// </remarks>
    private static void ConfigureLogging(WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog(
            (context, services, configuration) => configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty(
                    "Application",
                    context.Configuration["Observability:ServiceName"] ?? "AI.Investment.Api")
                .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .WriteTo.Console(),
            preserveStaticLogger: true);
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        // ---- Configuration -------------------------------------------------------------
        // Validated at start-up. A misconfigured deployment must not begin accepting traffic.
        builder.Services.AddValidatedOptions<ObservabilityOptions>(
            builder.Configuration,
            ObservabilityOptions.SectionName);

        // What this instance runs on its own. Both activities default to off - see
        // DataPlaneOptions for why - and an out-of-range interval or batch size stops start-up
        // rather than being discovered when the first sweep behaves strangely at 3am.
        builder.Services.AddValidatedOptions<DataPlaneOptions>(
            builder.Configuration,
            DataPlaneOptions.SectionName);

        // ---- Application and infrastructure ---------------------------------------------
        // The ONLY place in this project permitted to reference AI.Investment.Infrastructure.
        // An architecture test fails the build if an Infrastructure type is used anywhere else.
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);

        // Infrastructure binds and validates its own options; whether an invalid value should
        // prevent this host from accepting traffic is a decision for the host, and it is made
        // here. A misconfigured deployment fails at start-up rather than on the first request
        // that happens to read the setting - which, once background processing exists, could be
        // hours later and on a different machine.
        builder.Services.AddOptions<DatabaseOptions>().ValidateOnStart();

        // Adapter between the transport and the application's correlation abstraction.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICorrelationContext, HttpCorrelationContext>();

        // ---- Background work -------------------------------------------------------------
        // Both read DataPlaneOptions and return immediately when their activity is disabled,
        // which it is by default. Registering them unconditionally keeps the decision in
        // configuration rather than splitting it between here and there, and means the log says
        // "disabled" rather than saying nothing at all.
        //
        // Seeding is what makes the data plane usable: until something calls it the registry
        // starts empty and every ingestion run refuses. The sweep is the only activity in the
        // platform that destroys evidence - enable it on exactly one instance.
        builder.Services.AddHostedService<SourceSeedingHostedService>();
        builder.Services.AddHostedService<RetentionSweepHostedService>();

        // ---- Web -------------------------------------------------------------------------
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

        builder.Services.AddHealthChecks()
            .AddCheck(
                "self",
                () => HealthCheckResult.Healthy("API host is running"),
                tags: LivenessTags)
            .AddCheck<DatabaseHealthCheck>(
                "postgresql",
                failureStatus: HealthStatus.Unhealthy,
                tags: ReadinessTags);
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        // Order matters: correlation first, so every subsequent log event and every error
        // response carries the identifier.
        app.UseCorrelationId();
        app.UseExceptionHandler();

        app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                if (httpContext.Items.TryGetValue(
                        CorrelationIdMiddleware.HttpContextItemKey,
                        out var correlationId))
                {
                    diagnosticContext.Set("CorrelationId", correlationId);
                }
            };
        });

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        else
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        // NOTE: authentication and authorization are deliberately NOT registered.
        // The pre-Phase-0 solution called UseAuthorization() with no authentication scheme,
        // which is a no-op that reads as security in review (audit finding F-03). Real
        // OIDC/JWT authentication is scheduled work; an honest absence is safer than a
        // decorative call. Until it exists, do not expose this API beyond localhost.
        // See docs/SECURITY.md.

        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
        });

        // Readiness: the database must be reachable before this instance takes traffic.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
        });

        // Aggregate endpoint: every registered check.
        app.MapHealthChecks("/health");

        app.MapControllers();
    }
}
