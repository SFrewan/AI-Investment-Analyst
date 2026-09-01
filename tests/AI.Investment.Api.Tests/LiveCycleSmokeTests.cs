using System.Globalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Operations;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Watching;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Boots the real API host against the real database and the real providers.
/// </summary>
/// <remarks>
/// <para>
/// The opposite of <see cref="ApiFactory"/> in every way that matters. That one points at an
/// unreachable database on purpose, because the tests it serves are about the HTTP pipeline. This
/// one runs the Development configuration - the operator's own connection string and provider
/// credentials, out of user secrets - because the only thing it is for is proving that the live
/// path works end to end.
/// </para>
/// <para>
/// <strong>The scheduler is off.</strong> <c>OperationsHost:RunCycles</c> is forced to false so the
/// host starts nothing of its own: the one cycle this fixture runs is the one the test creates and
/// drives by hand. A background loop ticking every thirty seconds alongside a live-provider test
/// would make the number of billable requests a function of how long the test took.
/// </para>
/// <para>
/// User secrets are added explicitly rather than relied upon. The web host resolves them from the
/// application name, which a test host sets differently from a console launch, and a silently
/// missing connection string would surface as a confusing failure deep inside the first query
/// rather than as the configuration problem it is.
/// </para>
/// </remarks>
public sealed class LiveCycleFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Nothing runs on a timer. See the class remarks.
                ["OperationsHost:RunCycles"] = "false",
                ["OperationsHost:RunOutboxDispatcher"] = "false",
            });
        });
    }
}

/// <summary>
/// Runs one real operating cycle, immediately, without touching a watch's cooldown.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Proving the pipeline and proving the cooldown are two
/// different questions, and using the second to gate the first means a four-hour wait for every
/// answer about the first. The production cooldown is unchanged and untouchable from here: it lives
/// in <see cref="Watch.Evaluate"/>, which is reached only from <see cref="TriggerEvaluator"/>, and
/// nothing in this file calls either. <see cref="Watch.RecordFiring"/> is never invoked, so
/// <see cref="Watch.LastFiredAtUtc"/> and <see cref="Watch.FireCount"/> do not move - and the test
/// asserts that they did not, so a future change that started routing through the evaluator would
/// fail here rather than quietly consuming an operator's firing budget.
/// </para>
/// <para>
/// <strong>What is bypassed, and what is not.</strong> Only trigger evaluation and admission
/// control - the two controls whose job is to bound a storm of cycles. This creates exactly one, so
/// there is no storm to bound. Everything downstream is the production path unmodified: the same
/// <see cref="ICycleStore"/>, the same <see cref="ICycleBudgetProvider"/>, the same
/// <see cref="OperatingCycleRunner"/>, the same work plan, the same write guard, the same
/// <c>IActionGateway</c> and policy evaluation, and the same idempotency keys. A cycle produced
/// here is indistinguishable from a scheduled one except for the shape of its trigger key.
/// </para>
/// <para>
/// <strong>Three independent guards against a repeated bill.</strong> First, the test does not run
/// unless <c>AIINV_LIVE_SMOKE</c> is set to 1, so a normal <c>dotnet test</c> never reaches a
/// provider. Second, the trigger key is stamped with the current UTC hour, and the cycle store's
/// unique index on it means a second run inside the same hour resumes the cycle that already
/// exists instead of starting a new one. Third, that resumed cycle re-derives the same idempotency
/// key - the request fingerprint scoped to <c>cycle-{cycleId}</c> - so the seam suppresses the
/// repeat fetch even if the cycle is re-driven. To deliberately buy a second cycle, wait for the
/// next hour or set <c>AIINV_LIVE_SMOKE_KEY</c>.
/// </para>
/// </remarks>
public sealed class LiveCycleSmokeTests
{
    /// <summary>Set to 1 to permit exactly one live cycle. Absent everywhere but an operator's shell.</summary>
    private const string GateVariable = "AIINV_LIVE_SMOKE";

    /// <summary>Optional. Names the watch to review, when more than one is configured.</summary>
    private const string WatchVariable = "AIINV_LIVE_SMOKE_WATCH";

    /// <summary>Optional. Overrides the hourly key, to buy a second cycle deliberately.</summary>
    private const string KeyVariable = "AIINV_LIVE_SMOKE_KEY";

    private const string Worker = "live-smoke";

    [SkippableFact]
    public async Task One_live_cycle_reaches_Record_and_leaves_the_watch_cooldown_alone()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal),
            $"Live verification is off. Set {GateVariable}=1 to run exactly one real cycle against " +
            "the configured database and provider. This test makes a billable request.");

        await using var factory = new LiveCycleFactory();

        // Touching Services builds and starts the host, which is what runs the hosted services -
        // source seeding in particular - before anything asks the registry for a source.
        var root = factory.Services;

        Watch watch;
        Guid cycleId;
        bool fresh;
        IngestionSubject subject;
        int observationsBefore;
        CycleRunResult result;

        using (var scope = root.CreateScope())
        {
            var services = scope.ServiceProvider;
            var clock = services.GetRequiredService<IClock>();
            var now = clock.UtcNow;

            watch = await FindWatchAsync(services.GetRequiredService<IWatchStore>());

            subject = IngestionSubject.Create(watch.Target.Kind, watch.Target.Identifier);

            observationsBefore = (await services
                .GetRequiredService<IObservationStore>()
                .ForSubjectAsync(subject, now)).Count;

            (cycleId, fresh) = await StartOrResumeAsync(services, watch, now);
        }

        // A second scope for the run, so the cycle the runner loads is read from the database rather
        // than handed to it by the change tracker - the same way a worker picking it up would.
        using (var scope = root.CreateScope())
        {
            result = await scope.ServiceProvider
                .GetRequiredService<OperatingCycleRunner>()
                .RunAsync(cycleId, Worker);
        }

        // And a third for the verification, for the same reason: every assertion below has to be
        // about what was persisted, not about objects this test still holds a reference to.
        using (var scope = root.CreateScope())
        {
            await AssertPipelineRanAsync(
                scope.ServiceProvider, cycleId, subject, observationsBefore, fresh, result);
            await AssertCooldownUntouchedAsync(scope.ServiceProvider, watch);
        }
    }

    /// <summary>
    /// The watch whose instrument this cycle reviews.
    /// </summary>
    /// <remarks>
    /// A watch, and not a symbol from configuration, because the work plan resolves the subject from
    /// the cycle's watch. A cycle with no watch behind it is blocked at Discover by design, so an
    /// isolated cycle has to carry a real one - which is exactly why this file never needed to touch
    /// the watch's firing state to get a subject.
    /// </remarks>
    private static async Task<Watch> FindWatchAsync(IWatchStore watches)
    {
        var all = await watches.GetAllAsync();

        var named = Environment.GetEnvironmentVariable(WatchVariable);

        var candidates = all
            .Where(w => string.Equals(w.CycleTemplate, EquityReviewWorkPlan.Template, StringComparison.Ordinal))
            .Where(w => !string.IsNullOrWhiteSpace(w.Target.Identifier))
            .ToList();

        var watch = string.IsNullOrWhiteSpace(named)
            ? candidates.FirstOrDefault()
            : candidates.FirstOrDefault(w =>
                string.Equals(
                    w.WatchId.ToString("d", CultureInfo.InvariantCulture),
                    named.Trim(),
                    StringComparison.OrdinalIgnoreCase));

        Skip.If(
            watch is null,
            $"No enabled '{EquityReviewWorkPlan.Template}' watch naming an instrument was found" +
            (string.IsNullOrWhiteSpace(named) ? "." : $" for {WatchVariable}={named}.") +
            " Schedule one before running the live verification.");

        return watch!;
    }

    /// <summary>
    /// The cycle to drive: a new one, or the one this hour already started.
    /// </summary>
    /// <remarks>
    /// The hourly trigger key is the guard that makes a re-run cheap rather than billable. The store
    /// refuses a duplicate key in the database, so two processes racing this produce one cycle, and
    /// running the test twice in five minutes resumes the first rather than buying a second.
    /// </remarks>
    private static async Task<(Guid CycleId, bool Fresh)> StartOrResumeAsync(
        IServiceProvider services,
        Watch watch,
        DateTime now)
    {
        var cycles = services.GetRequiredService<ICycleStore>();

        var stamp = Environment.GetEnvironmentVariable(KeyVariable) is { Length: > 0 } custom
            ? custom
            : now.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);

        var triggerKey = string.Create(
            CultureInfo.InvariantCulture,
            $"live-smoke:{watch.WatchId:d}:{stamp}");

        if (await cycles.FindByTriggerKeyAsync(triggerKey) is { } existing)
        {
            return (existing.CycleId, Fresh: false);
        }

        var budget = await services.GetRequiredService<ICycleBudgetProvider>().GetAsync(watch.CycleTemplate);

        var cycle = OperatingCycle.Start(
            CorrelationId.Create(string.Create(CultureInfo.InvariantCulture, $"live-smoke-{Guid.NewGuid():N}")),
            watch.Capability,
            watch.CycleTemplate,
            triggerKey,
            budget,
            budget.MaxModelSpend.Currency,
            now,

            // The watch id is what lets Discover resolve the instrument. Carrying it is not the same
            // as firing the watch: nothing here calls Evaluate or RecordFiring.
            watch.WatchId);

        Assert.True(
            await cycles.TryAddAsync(cycle),
            $"the cycle store refused trigger key '{triggerKey}' although no cycle was found under it.");

        return (cycle.CycleId, Fresh: true);
    }

    private static async Task AssertPipelineRanAsync(
        IServiceProvider services,
        Guid cycleId,
        IngestionSubject subject,
        int observationsBefore,
        bool fresh,
        CycleRunResult result)
    {
        var cycle = await services.GetRequiredService<ICycleStore>().FindAsync(cycleId);

        Assert.NotNull(cycle);

        // Reported and persisted separately, because a runner that returned Completed without
        // writing it down would be the defect worth catching.
        Assert.Equal(CycleStatus.Completed, result.Status);
        Assert.Equal(CycleStatus.Completed, cycle!.Status);
        Assert.Equal(CycleStages.Last, cycle.Stage);
        Assert.Equal(CycleStage.Record, cycle.Stage);

        // A pass whose provider failed completes too - it escalates on the way out. Asserting the
        // absence of that escalation is what separates "it ran" from "it worked".
        Assert.False(
            result.Escalated,
            $"the cycle escalated: {result.Summary}. A live pass that reached Record by way of an " +
            "escalation has not proved the pipeline.");

        // The fetch. Matched on the work plan's own cycle-scoped correlation, so this is this
        // cycle's run and not a neighbour's.
        var correlation = string.Create(CultureInfo.InvariantCulture, $"cycle-{cycleId:N}");

        var runs = await services
            .GetRequiredService<IIngestionRunStore>()
            .GetRecentAsync(cycle.StartedAtUtc.AddMinutes(-1), 100);

        var run = runs.FirstOrDefault(r =>
            string.Equals(r.Request.CorrelationId.Value, correlation, StringComparison.Ordinal));

        Assert.True(
            run is not null,
            $"no ingestion run was recorded for correlation '{correlation}'. The cycle reached " +
            "Record without asking a provider for anything.");

        Assert.Equal(IngestionOutcome.Succeeded, run!.Outcome);
        Assert.Null(run.RefusalRuleId);

        // The subject as it was persisted with the run, not as this test constructed it.
        Assert.Equal(subject.Kind, run.Request.Subject.Kind);
        Assert.Equal(subject.Identifier, run.Request.Subject.Identifier);

        var stored = await services
            .GetRequiredService<IObservationStore>()
            .ForSubjectAsync(subject, DateTime.UtcNow);

        // Growth is the assertion only when this run started the cycle. A run that resumed the cycle
        // an earlier run of this hour already completed re-checks the same evidence and buys nothing,
        // so demanding growth there would fail on a working pipeline.
        if (fresh)
        {
            Assert.True(
                stored.Count > observationsBefore,
                $"the subject had {observationsBefore} observations before the cycle and " +
                $"{stored.Count} after. A successful fetch that persisted nothing is the failure " +
                "this verification exists to catch.");
        }
        else
        {
            Assert.True(
                stored.Count > 0,
                "this run resumed a cycle an earlier run of this hour had already completed, and " +
                "the subject has no observations at all. Re-run in the next hour, or set " +
                "AIINV_LIVE_SMOKE_KEY, to buy a fresh cycle.");
        }

        // Every row carries its own subject. The owned value is associated with an owner by
        // reference, so a normaliser sharing one instance across a batch used to leave all but the
        // first without one - which the database refused, and which no unit test can see.
        Assert.DoesNotContain(stored, o => string.IsNullOrWhiteSpace(o.Subject.Kind));
        Assert.DoesNotContain(stored, o => string.IsNullOrWhiteSpace(o.Subject.Identifier));
    }

    /// <summary>
    /// That the production cooldown is exactly where it was.
    /// </summary>
    /// <remarks>
    /// The whole point of the isolated path. If a later refactor routed this through the trigger
    /// evaluator to save a few lines, the firing count would move and this would fail - which is
    /// the warning worth having, because the alternative is a verification harness quietly spending
    /// an operator's firing budget every time somebody runs the tests.
    /// </remarks>
    private static async Task AssertCooldownUntouchedAsync(IServiceProvider services, Watch before)
    {
        var after = await services.GetRequiredService<IWatchStore>().FindAsync(before.WatchId);

        Assert.NotNull(after);
        Assert.Equal(before.FireCount, after!.FireCount);
        Assert.Equal(before.LastFiredAtUtc, after.LastFiredAtUtc);
        Assert.Equal(before.Cooldown, after.Cooldown);
        Assert.Equal(before.Enabled, after.Enabled);
    }
}
