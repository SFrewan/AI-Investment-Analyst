using AI.Investment.Application.Actions;
using AI.Investment.Application.Autonomy;
using AI.Investment.Application.Capital;
using AI.Investment.Application.Companies.CreateCompany;
using AI.Investment.Application.Companies.GetCompany;
using AI.Investment.Application.Companies.SearchCompanies;
using AI.Investment.Application.Freshness;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Operations;
using AI.Investment.Application.Operators;
using AI.Investment.Application.Opportunities;
using AI.Investment.Application.Validation;
using AI.Investment.Application.Retention;
using AI.Investment.Application.Sources.ActivateSource;
using AI.Investment.Application.Sources.RegisterKnownSources;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Opportunities;
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

        // The caller the evaluator never had. Schedule is the one trigger type that describes
        // nothing arriving, so without this a scheduled watch is stored, enabled and never fired.
        services.AddScoped<ScheduleTicker>();
        services.AddScoped<OperatingCycleRunner>();

        // ---- The work the loop runs. -----------------------------------------------------------
        //
        // Phase 6 shipped no cycle work plan, so every cycle escalated and suspended with "no work
        // plan is registered" - correct, fail-closed, and productive of nothing for the validation
        // run to measure. These three are the smallest thing that changes that: one reader of stored
        // closes, one discoverer that screens them, and one plan that sequences the pass and
        // proposes recording what it found.
        //
        // All scoped, and deliberately: the plan carries the state of the cycle it is driving, and
        // the discoverer keeps the reason it last found nothing. Neither may be shared between
        // concurrent cycles.
        //
        // What is proposed is a record under OpportunityManagement with no financial effect. Nothing
        // here places an order, reaches a venue, or raises autonomy.
        services.AddScoped<PriceSeriesReader>();
        services.AddScoped<IOpportunityDiscoverer, PriceRecoveryDiscoverer>();
        services.AddScoped<ICycleWorkPlan, EquityReviewWorkPlan>();

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

        // ---- The operator surface. Development block 1. ---------------------------------------
        //
        // Scoped, because it acts on behalf of one authenticated person for the duration of one
        // request. Every method proposes through the action gateway as ProposedBy.Human, so the
        // audit record's actor is a person rather than a service - which is the whole reason these
        // operations did not exist before there was an identity to record.
        //
        // Notably absent is anything that approves an action or disengages the kill switch. Both are
        // documented refusals rather than omissions; see OperatorConsole.
        services.AddScoped<OperatorConsole>();

        services.AddScoped<RegisterKnownSourcesHandler>();
        services.AddScoped<ActivateSourceHandler>();

        services.AddScoped<CreateCompanyHandler>();
        services.AddScoped<GetCompanyHandler>();
        services.AddScoped<SearchCompaniesHandler>();

        return services;
    }
}
