using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ai;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Application.Ai.Agents;
using AI.Investment.Application.Ai.Pipeline;
using AI.Investment.Application.Approvals;
using AI.Investment.Application.Execution;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Opportunities;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Ai;
using AI.Investment.Infrastructure.Auditing;
using AI.Investment.Infrastructure.Execution;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Application.Operations;
using AI.Investment.Application.Validation;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Operations;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using AI.Investment.Infrastructure.Policy;
using AI.Investment.Infrastructure.Time;
using AI.Investment.Infrastructure.Validation;
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
        AddAi(services, configuration);
        AddOpportunities(services);
        AddOperations(services, configuration);
        AddValidation(services, configuration);

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

        services.AddOptions<PromptStoreOptions>()
            .Bind(configuration.GetSection(PromptStoreOptions.SectionName))
            .ValidateDataAnnotations();

        // Same reasoning as SafetyOptions: a misconfigured ceiling should leave the platform running
        // and refusing, where an operator can see it, rather than preventing the host from starting.
        // ConfiguredLimitProvider returns a set that refuses everything when it cannot read this.
        services.AddOptions<LimitOptions>()
            .Bind(configuration.GetSection(LimitOptions.SectionName));

        services.AddOptions<SimulatedVenueOptions>()
            .Bind(configuration.GetSection(SimulatedVenueOptions.SectionName))
            .ValidateDataAnnotations();
    }

    /// <summary>
    /// Registers the opportunity, approval, capital and execution machinery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SimulatedVenue"/> is the only <c>IExecutionVenue</c> registered, and registering a
    /// real one is a separate, formal decision gated behind the validation phase - not a
    /// configuration switch. <c>ExecutionRuleTests</c> asserts by reflection that every type in the
    /// solution implementing <c>IExecutionVenue</c> reports itself simulated, so adding a live one
    /// cannot happen quietly.
    /// </para>
    /// <para>
    /// The kill switch is registered scoped because it reads the database. Its environment half
    /// needs no state at all, which is what lets it answer when the database cannot.
    /// </para>
    /// </remarks>
    private static void AddOpportunities(IServiceCollection services)
    {
        services.AddScoped<IOpportunityRepository, EfOpportunityRepository>();
        services.AddScoped<IApprovalTokenStore, EfApprovalTokenStore>();
        services.AddScoped<ILedgerStore, EfLedgerStore>();
        services.AddScoped<IExposureProvider, LedgerExposureProvider>();
        services.AddScoped<IKillSwitch, DatabaseAndEnvironmentKillSwitch>();
        services.AddSingleton<ILimitProvider, ConfiguredLimitProvider>();

        services.AddScoped<IExecutionVenue, SimulatedVenue>();

        // The first concrete opportunity type. A type is two registrations - what it must prove
        // before it may leave Draft, and how its economics are calculated - and nothing else. The
        // lifecycle, approvals, limits, ledger and audit trail are untouched by adding a second.
        services.AddSingleton<IEvidenceRequirement, EquityEvidenceRequirement>();
        services.AddSingleton<IOpportunityEconomicsCalculator, EquityEconomicsCalculator>();

        services.AddScoped<OpportunityWorkflow>();
        services.AddScoped<ApprovalWorkflow>();
        services.AddScoped<OpportunityExecutor>();
    }

    /// <summary>
    /// Registers the AI layer, with a provider that refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="UnconfiguredChatModel"/> is the registered <c>IChatModel</c> and, in this phase,
    /// the only one. Phase 4 delivers the port, the agents, the validators and the evaluation
    /// harness; the adapter that calls a paid provider belongs to the phase that decides to spend
    /// money, because spending money is an action this platform gates rather than assumes.
    /// </para>
    /// <para>
    /// Registering a refusing model rather than nothing at all is deliberate. An unregistered
    /// dependency fails when something resolves it, which is a stack trace at an arbitrary moment;
    /// a refusing one produces an audited <c>ProviderError</c> at the point of use, which is a
    /// record an operator can read.
    /// </para>
    /// </remarks>
    private static void AddAi(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IPromptStore, FilePromptStore>();
        services.AddSingleton<IChatModel, UnconfiguredChatModel>();

        services.AddScoped<IAnalysisAgent<EvidenceBundle, FinancialReading>, FinancialAnalysisAgent>();
        services.AddScoped<IAnalysisAgent<EvidenceBundle, NewsReading>, NewsAnalysisAgent>();
        services.AddScoped<IAnalysisAgent<EvidenceBundle, RiskAssessment>, RiskAnalysisAgent>();
        services.AddScoped<IAnalysisAgent<SynthesisInput, AnalysisSynthesis>, SynthesisAgent>();

        services.AddScoped<AnalysisPipeline>();
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
        services.AddScoped<IObservationStore, EfObservationStore>();
        services.AddScoped<IQuarantineStore, EfQuarantineStore>();
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
        AddNormalizers(services);
    }

    /// <summary>Registers the normalisers that read archived payloads.</summary>
    /// <remarks>
    /// <para>
    /// Deliberately unconditional, and deliberately not inside a connector's registration. A
    /// normaliser reads bytes that are already in the archive; whether the connector that fetched
    /// them is currently enabled has nothing to do with whether they can still be read. Tying the
    /// two together would mean that turning EDGAR off quarantined every payload it had ever
    /// retrieved, under a rule saying no normaliser existed - which would be false.
    /// </para>
    /// <para>
    /// Singletons: a normaliser is a pure function from bytes to observations and holds no state.
    /// </para>
    /// <para>
    /// The pipeline resolves <c>IEnumerable&lt;INormalizer&gt;</c> and asks each whether it reads a
    /// given source and category, so a new normaliser is one line here and nothing else.
    /// </para>
    /// </remarks>
    private static void AddNormalizers(IServiceCollection services)
    {
        services.AddSingleton<INormalizer, SecEdgarSubmissionsNormalizer>();
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

        // The connector also knows its source's authority, licensing, coverage and cadence, so it
        // ships the registry definition rather than leaving an operator to re-type a regulator's
        // terms and get one of them subtly wrong. Registered inactive; seeding registers, an
        // operator activates.
        services.AddSingleton<ISourceDefinition, SecEdgarSource>();
    }

    private static void AddSafety(IServiceCollection services)
    {
        // Scoped: an authorisation window belongs to one request or one operating cycle and must
        // not leak across them.
        services.AddScoped<IWriteAuthorization, ScopedWriteAuthorization>();

        // Scoped for the same reason, and it is the same kind of thing: an ambient fact about the
        // work in flight that must not leak into work started beside it. The cycle runner opens one
        // around a dispatch; outside it, a cycle-driven proposal is refused by the policy engine.
        services.AddScoped<IAutonomyContext, AutonomyContext>();

        services.AddScoped<IPolicyContextProvider, ConfiguredPolicyContextProvider>();
        services.AddScoped<IAuditSink, EfAuditSink>();
        services.AddScoped<IActionExecutionStore, EfActionExecutionStore>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
    }
    /// <summary>
    /// Registers continuous operation: watches, cycles, grants, escalations, shadow measurement and
    /// the outbox.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every store is scoped, because each shares the request-scoped context and therefore the
    /// transaction. That is what makes the outbox transactional rather than merely a table: the
    /// message and the change that caused it are staged on the same context and commit together.
    /// </para>
    /// <para>
    /// The four notification handlers deliver into the append-only audit trail, which is the
    /// destination this phase has. There is deliberately no email, pager or chat integration here:
    /// inventing a notification plane on the way past is how one ends up with an unconfigurable one.
    /// </para>
    /// </remarks>
    /// <summary>The message types that are delivered into the audit trail.</summary>
    private static readonly string[] NotifiedMessageTypes =
    [
        OperationsMessages.EscalationRaised,
        OperationsMessages.CycleFinished,
        OperationsMessages.ShadowDecisionRecorded,
        OperationsMessages.OutboxAbandoned,
    ];

    /// <summary>
    /// Registers the validation read side. Phase 7.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT ValidateOnStart, for the same reason as the sections above: a mistyped
    /// evaluation window should leave the platform running and the validation endpoint reporting
    /// that it cannot run, where somebody can see it, rather than preventing the host from starting.
    /// </remarks>
    private static void AddValidation(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ValidationOptions>()
            .Bind(configuration.GetSection(ValidationOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddSingleton<IValidationRequestFactory, ConfiguredValidationRequestFactory>();
        services.AddScoped<IValidationHistory, EfValidationHistory>();
        services.AddScoped<IPredictionCatalogue, EfPredictionCatalogue>();
    }

    private static void AddOperations(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Deliberately NOT ValidateOnStart, for the same reason as SafetyOptions: a misconfigured
        // ceiling should leave the platform running and failing closed, where an operator can see
        // it, rather than preventing the host from starting at all.
        services.AddOptions<OperationsOptions>()
            .Bind(configuration.GetSection(OperationsOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddScoped<IAutonomyGrantStore, EfAutonomyGrantStore>();
        services.AddScoped<IWatchStore, EfWatchStore>();
        services.AddScoped<ICycleStore, EfCycleStore>();
        services.AddScoped<IEscalationStore, EfEscalationStore>();
        services.AddScoped<IShadowDecisionStore, EfShadowDecisionStore>();

        // Phase 8. Both tables are expected to be empty: no warrant can be issued while the measured
        // evidence does not justify one, and no live venue can be authorised without a warrant.
        services.AddScoped<IPromotionWarrantStore, EfPromotionWarrantStore>();
        services.AddScoped<ILiveVenueAuthorizationStore, EfLiveVenueAuthorizationStore>();

        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();

        services.AddSingleton<IAdmissionLimitProvider, ConfiguredAdmissionLimitProvider>();
        services.AddSingleton<ICycleBudgetProvider, ConfiguredCycleBudgetProvider>();

        foreach (var messageType in NotifiedMessageTypes)
        {
            var type = messageType;

            services.AddScoped<IOutboxHandler>(provider => new AuditNotificationHandler(
                type,
                provider.GetRequiredService<IAuditSink>(),
                provider.GetRequiredService<IClock>()));
        }
    }
}
