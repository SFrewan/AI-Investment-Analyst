using AI.Investment.Application.Actions;
using AI.Investment.Application.Autonomy;
using AI.Investment.Application.Capital;
using AI.Investment.Application.Companies.CreateCompany;
using AI.Investment.Application.Companies.GetCompany;
using AI.Investment.Application.Companies.SearchCompanies;
using AI.Investment.Application.Freshness;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Application.Operations;
using AI.Investment.Application.Validation;
using AI.Investment.Application.Retention;
using AI.Investment.Application.Sources.ActivateSource;
using AI.Investment.Application.Sources.RegisterKnownSources;
using AI.Investment.Domain.Actions;
using Microsoft.Extensions.DependencyInjection;

namespace AI.Investment.Application;

/// <summary>Registers the application layer's services.</summary>
/// <remarks>
/// Handlers are registered explicitly rather than by assembly scanning. Scanning is convenient
/// and hides what is registered; at this size an explicit list is shorter than the scanner
/// configuration would be, and a missing registration fails at start-up rather than at the
/// first request.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The policy engine is pure and holds no state, so a singleton is correct.
        services.AddSingleton<IPolicyEngine, PolicyEngine>();

        // The gateway is scoped: it participates in the same unit of work as the request.
        services.AddScoped<IActionGateway, ActionGateway>();

        // Scoped like the action gateway they build on: each participates in one unit of work.
        services.AddScoped<IIngestionGateway, IngestionGateway>();

        // Retention deletes evidence, so it is proposed under its own capability and denied by
        // default - a capability with no configured policy never executes.
        services.AddScoped<IRetentionEnforcer, RetentionEnforcer>();

        // The recurring half: decides when to walk the archive, never what may be deleted.
        services.AddScoped<IRetentionSweep, RetentionSweep>();

        // Fetch and interpret as one operation, so no caller has to remember to do both.
        services.AddScoped<IDataAcquisition, DataAcquisitionService>();

        // Read-only, and deliberately outside the seam: asking how current the data is has no
        // side effect, and auditing reads would bury the record of what actually changed.
        services.AddScoped<IFreshnessReport, FreshnessReport>();

        // The capital read model. Read only: there is no write path on ILedgerReport, and a balance
        // is a projection of immutable entries rather than a field anything can set.
        services.AddScoped<ILedgerReport, LedgerReport>();

        // The second half of ingesting: what the archived bytes mean. Scoped, and constructable
        // with no normalisers registered at all - an installation with none quarantines every
        // payload under normalization.no-normalizer@1 rather than failing to start, which keeps
        // "we cannot read this source yet" a recorded fact instead of a dead host.
        services.AddScoped<INormalizationPipeline, NormalizationPipeline>();

        // ---- Continuous operation. Phase 6. -------------------------------------------------
        //
        // All scoped, because each participates in one unit of work: a cycle's progress, the
        // escalation it raised and the message announcing it commit together or not at all.
        //
        // Notably absent is any registration of a cycle work plan. Phase 6 builds the loop and
        // ships no analytical plan for it to run: a template with none registered escalates and
        // suspends rather than quietly doing nothing.
        services.AddScoped<EscalationService>();
        services.AddScoped<ShadowRecorder>();
        services.AddScoped<TriggerEvaluator>();
        services.AddScoped<OperatingCycleRunner>();

        // Issuing, withdrawing and automatically lowering grants. Every method proposes an action
        // under AutonomyAdministration, which an AI proposer is refused structurally.
        services.AddScoped<AutonomyAdministration>();

        // ---- Validation. Phase 7. ------------------------------------------------------------
        //
        // Measurement only, and one registration: the replay engine is a static pure function like
        // every other decision in this system, so there is nothing to register for it.
        //
        // Nothing here writes. There is no path from a validation result back into a threshold, a
        // score or a policy, which is what keeps this phase measurement rather than fitting.
        services.AddScoped<ValidationService>();

        // ---- Bounded autonomy. Phase 8. -------------------------------------------------------
        //
        // The promotion gate, the live-venue gate and the circuit breaker. All scoped, because each
        // participates in one unit of work: a warrant, the audit record announcing it and the grant
        // written under it commit together or not at all.
        //
        // Notably absent is anything that promotes. PromotionService assesses and, on a named
        // person's decision and evidence assessed at that moment, issues a warrant; writing a grant
        // under it is a separate act through AutonomyAdministration. Nothing here raises autonomy on
        // its own, and AutonomyCircuitBreaker only ever lowers it.
        services.AddScoped<PromotionService>();
        services.AddScoped<LiveVenueService>();
        services.AddScoped<AutonomyCircuitBreaker>();

        services.AddScoped<RegisterKnownSourcesHandler>();
        services.AddScoped<ActivateSourceHandler>();

        services.AddScoped<CreateCompanyHandler>();
        services.AddScoped<GetCompanyHandler>();
        services.AddScoped<SearchCompaniesHandler>();

        return services;
    }
}
