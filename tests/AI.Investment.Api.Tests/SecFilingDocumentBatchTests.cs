using System.Text.Json;
using AI.Investment.Application.Sources.ReconcileSourceCoverage;
using AI.Investment.Domain.Sources;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The two deliberate acts that can reach the SEC for a filing document, each behind its own door.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Both are skipped unless deliberately opened.</strong> Admission and dispatch are separate
/// variables because they are separate decisions: widening a registry row admits no data on its own,
/// and a batch that is not approved refuses at the runner's first gate whatever the registry says.
/// </para>
/// <para>
/// Everything else in this file is read-only and runs always: the wiring, the partition state and
/// the invariants that must hold whether or not a batch ever ran.
/// </para>
/// </remarks>
public sealed class SecFilingDocumentBatchTests : IClassFixture<UniverseApiFactory>
{
    /// <summary>Cached, because an options instance per serialisation is wasteful and flagged.</summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SecFilingDocumentBatchTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    /// <summary>
    /// One approved filing-document batch, acquired through the real host. Off unless opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the only path in the repository that could fetch a filing document, and it is
    /// shut four times over.</strong> The gate variable is unset, so the fact is skipped. Were it
    /// set, the batch would still be unauthorised and the runner would refuse at its first gate.
    /// Were a batch authorised, the authorisation would still have to name every document and still
    /// be in date. Were all of that true, the connector would still have to be enabled with a
    /// deployment identity. Every one of those refusals costs nothing.
    /// </para>
    /// <para>
    /// There is no default batch: a run that does not say which batch it is must not pick one.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task One_approved_filing_document_batch_is_acquired_and_nothing_else_is()
    {
        Skip.IfNot(
            SecFilingDocumentBatchDoor.IsOpen(Environment.GetEnvironmentVariable),
            $"The filing-document batch door is shut. Set {SecFilingDocumentBatchDoor.GateVariable}=1 "
            + $"and {SecFilingDocumentBatchDoor.IndexVariable} to a batch number to open it. It SPENDS "
            + "AUTHORISATION UNITS and makes requests to a U.S. government service.");

        var batch = SecFilingDocumentBatchDoor.Batch(Environment.GetEnvironmentVariable);

        Assert.True(
            batch is not null,
            Universe.Inv($"{SecFilingDocumentBatchDoor.IndexVariable} does not name a batch in the partition, which has {SecEdgarFilingDocumentPartition.Batches.Length}. There is no default."));

        var authorization = SecFilingDocumentBatchDoor.LoadAuthorization();

        Assert.Equal(SecEdgarFilingDocumentPartition.AuthorizationId, authorization.AuthorizationId);
        Assert.Equal(SecFilingDocumentBatchDoor.ApprovedDigest, authorization.Digest);

        // What earlier runs already spent, charged before anything is dispatched.
        var prior = await SecFilingDocumentBatchDoor.PriorConsumptionAsync();

        authorization.RecordPriorConsumption(prior);

        var attempt = SecFilingDocumentBatchDoor.AttemptNumber(batch!.Index);

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;

        // 4. The batch is the one it claims to be, and its targets come from the sealed declaration.
        var targets = SecEdgarFilingDocumentPartition.TargetsFor(batch.Index);

        Assert.Equal(batch.Count, targets.Count);
        Assert.True(batch.Authorised, Universe.Inv($"batch {batch.Index} is not approved. Nothing is dispatched for an unapproved batch."));
        Assert.All(targets, t => Assert.Contains(t, authorization.Symbols));

        // 5. It is in date, judged against the host's own clock.
        var clock = services.GetRequiredService<IClock>();
        var validity = authorization.IsValidAt(clock.UtcNow);

        Assert.True(validity.Allowed, validity.Reason ?? "the authorisation is not valid now.");

        // ═══ nothing above this line has written anything, anywhere ═══════════════════════════
        //
        // 6. ADMISSION. Last, because it is the only irreversible act in the sequence: the
        //    reconciliation unions and never narrows, and no production code path narrows a source's
        //    coverage afterwards. Everything that could refuse has refused by now, so opening it here
        //    cannot leave the category admitted with nothing fetched under it.
        //
        //    It goes through the action seam like any other act, so an engaged kill switch refuses
        //    the registry change too - the sequence cannot half-complete.
        var reconciled = await services
            .GetRequiredService<ReconcileSourceCoverageHandler>()
            .HandleAsync(SourceId.Create(SecFilingDocumentBatchRunner.Source));

        _output.WriteLine(Universe.Inv($"admission: {reconciled.SourceId} {reconciled.Status}. {reconciled.Reason}"));
        _output.WriteLine("categories: " + string.Join(", ", reconciled.Categories.Select(c => c.ToString())));

        Assert.Contains(DataCategory.RegulatoryFilingDocuments, reconciled.Categories);

        // Exactly one category was added and nothing was dropped.
        //
        // Asserted as a SET. The handler orders the union by enum ordinal, which is its own
        // business and not something this stage has an opinion about; comparing against a
        // hand-written sequence would fail on a reordering that changed nothing.
        DataCategory[] expected =
        [
            DataCategory.RegulatoryFilings,
            DataCategory.CompanyProfile,
            DataCategory.FinancialStatements,
            DataCategory.EarningsDisclosure,
            DataCategory.MarketWideDisclosure,
            DataCategory.RegulatoryFilingDocuments,
        ];

        Assert.Equal(expected.Length, reconciled.Categories.Count);
        Assert.Empty(expected.Except(reconciled.Categories));
        Assert.Empty(reconciled.Categories.Except(expected));

        // 7. DISPATCH, immediately, in the same process and the same scope.
        //
        // No assertion stands between here and the run. Everything that could stop the dispatch was
        // checked before step 6, and the only checks after it are the two above, which prove the
        // admission did what was intended and nothing more. An assertion here about the dispatch
        // rather than the admission would recreate the window F6g fell into: admitted, and nothing
        // fetched under it.
        var (runner, context) = await SecFilingDocumentBatchDoor.OpenAsync(
            services, batch, authorization, Environment.GetEnvironmentVariable, attempt);

        var outcome = await runner.RunAsync(context);

        // The immutable record, written whatever happened - a refusal is as much a fact as a fetch.
        await Universe.WriteAsync(
            SecFilingDocumentBatchDoor.ArtefactPathFor(batch.Index, attempt),
            JsonSerializer.Serialize(outcome, Indented));

        _output.WriteLine(Universe.Inv($"batch {outcome.BatchIndex} ({outcome.Cik} / {outcome.Symbol}) attempt {attempt}: {outcome.Status}. Dispatched {outcome.Dispatched}, archived {outcome.Archived}, consumed this run {outcome.ConsumedThisRun}, total {outcome.ConsumedTotal} of {authorization.DispatchCeiling}, remaining {outcome.Remaining}."));

        foreach (var target in outcome.Targets)
        {
            _output.WriteLine(Universe.Inv($"  {target.Target} -> {target.Status} {target.RunOutcome} {target.ContentHash} {target.ByteLength} {target.RuleId}"));
        }

        // The context carried exactly the declaration-derived targets and nothing else. Checked
        // here rather than before the run, because by now a failure cannot leave the system
        // admitted-but-unspent - and the runner's own runner.member-identity@1 gate already refuses
        // a foreign target before any request is built, at no cost.
        Assert.Equal(targets, context.Targets);
        Assert.Equal(targets, outcome.Targets.Select(t => t.Target).ToList());

        // Whatever the provider did, these hold.
        Assert.True(outcome.ConsumedThisRun <= batch.Count);
        Assert.True(authorization.Consumed <= authorization.DispatchCeiling);
        Assert.True(outcome.Targets.Count <= batch.Count);
        Assert.Equal(outcome.Dispatched, outcome.ConsumedThisRun);
    }

    // ================= always-on invariants =================

    /// <summary>Every dependency the runner needs comes out of the host the application composes.</summary>
    [Fact]
    public void The_runner_resolves_from_the_real_container()
    {
        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;

        var runner = new SecFilingDocumentBatchRunner(
            services.GetRequiredService<IDataAcquisition>(),
            services.GetRequiredService<IIngestionRunStore>(),
            services.GetRequiredService<IRawResponseArchive>(),
            services.GetRequiredService<IClock>());

        Assert.NotNull(runner);
    }

    /// <summary>The door is shut: no gate variable is set in an ordinary environment.</summary>
    [Fact]
    public void The_door_is_shut_unless_deliberately_opened()
    {
        static string? Nothing(string _) => null;

        Assert.False(SecFilingDocumentBatchDoor.IsOpen(Nothing));
        Assert.False(SecFilingDocumentBatchDoor.AdmissionRequested(Nothing));
        Assert.Null(SecFilingDocumentBatchDoor.Batch(Nothing));

        // And an index that names no batch yields none rather than a default.
        Assert.Null(SecFilingDocumentBatchDoor.Batch(v =>
            v == SecFilingDocumentBatchDoor.IndexVariable ? "7" : null));

        Assert.Null(SecFilingDocumentBatchDoor.Batch(v =>
            v == SecFilingDocumentBatchDoor.IndexVariable ? "not a number" : null));
    }

    /// <summary>The authorisation the door would load is the one F6c sealed.</summary>
    [Fact]
    public void The_door_loads_the_sealed_authorisation()
    {
        var authorization = SecFilingDocumentBatchDoor.LoadAuthorization();

        Assert.Equal(SecFilingDocumentBatchDoor.ApprovedDigest, authorization.Digest);
        Assert.Equal(30, authorization.DispatchCeiling);
        Assert.Equal(30, authorization.Symbols.Count);
        Assert.True(authorization.IsDocumentScoped);
    }
}
