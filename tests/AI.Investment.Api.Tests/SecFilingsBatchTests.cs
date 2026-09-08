using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The real execution door for the SEC runner, and the proof that it is shut.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The runtime path is the one that already exists.</strong> A script sets the connector
/// switch and two gate variables, <c>gate-tests.ps1</c> runs <c>dotnet test --filter</c>, the gated
/// fact below builds the real host through <see cref="UniverseApiFactory"/>, and every service comes
/// out of the container <c>Program.cs</c> composes. That is exactly how
/// <c>AcquisitionSplitBatchTests</c> and <c>AcquisitionBatchTests</c> are reached, and nothing else
/// in this repository reaches a provider at all. No second framework was built.
/// </para>
/// <para>
/// <strong>Two locks, and this phase opened neither.</strong> <c>Providers:SecEdgar:Enabled</c> is
/// <c>false</c> in the shipped configuration, so the connector is never registered and the preflight
/// refuses on <c>ingestion.provider-available@1</c>. And every batch is <c>Authorised: false</c>, so
/// the runner refuses at its first gate before a service is resolved. The facts below drive the real
/// container into each lock in turn and read the counter on the way out.
/// </para>
/// <para>
/// <strong>The gated fact is a <c>SkippableFact</c>, following the split runner exactly.</strong> A
/// normal run reports it as skipped rather than reaching a provider, which is why it is safe for it
/// to exist in the suite at all.
/// </para>
/// </remarks>
public sealed class SecFilingsBatchTests : IClassFixture<UniverseApiFactory>
{
    private const string ApprovedDigest =
        "499f211f9c931c78d7faf1beb76eef3205fec12cb28db06eeeda461964567682";

    private const string AuthorizationId = "sec-edgar-gate6-six-members-2021-09-to-2026-08";

    private static readonly string[] Ciks =
    [
        "0000892482", "0001335112", "0001404123", "0001426332", "0001775625", "0001784851",
    ];

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SecFilingsBatchTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    // ================= the door itself, shut =================

    /// <summary>
    /// One approved SEC batch, acquired through the real host. Skipped unless deliberately opened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the only path in the repository that could reach the SEC, and it is shut
    /// three times over.</strong> The gate variable is unset, so the fact is skipped. Were it set,
    /// the batch would still be unauthorised and the runner would refuse at its first gate. Were a
    /// batch authorised, the connector would still be unregistered and the preflight would refuse it.
    /// Every one of those refusals costs nothing.
    /// </para>
    /// <para>
    /// There is no default batch, following <c>AIINV_SPLIT_BATCH_INDEX</c>: a run that does not say
    /// which batch it is must not pick one.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task One_approved_sec_batch_is_acquired_and_nothing_else_is()
    {
        Skip.IfNot(
            SecFilingsBatchDoor.IsOpen(Environment.GetEnvironmentVariable),
            $"The SEC batch door is shut. Set {SecFilingsBatchDoor.GateVariable}=1 and "
            + $"{SecFilingsBatchDoor.IndexVariable} to a batch number to open it. It SPENDS AN "
            + "AUTHORISATION UNIT and makes a request to a U.S. government service.");

        var batch = SecFilingsBatchDoor.Batch(Environment.GetEnvironmentVariable);

        Assert.True(
            batch is not null,
            Universe.Inv($"{SecFilingsBatchDoor.IndexVariable} does not name a batch in the partition, which has {SecEdgarSixMemberPartition.Batches.Length}. There is no default."));

        var authorization = SecFilingsBatchDoor.LoadAuthorization();

        Assert.Equal(AuthorizationId, authorization.AuthorizationId);
        Assert.Equal(ApprovedDigest, authorization.Digest);

        // What earlier runs already spent, charged before anything is dispatched.
        var prior = await SecFilingsBatchDoor.PriorConsumptionAsync(authorization.AuthorizationId);

        authorization.RecordPriorConsumption(prior);

        var attempt = await AttemptNumberAsync(batch!.Index);

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;

        var source = await services
            .GetRequiredService<ISourceRegistry>()
            .GetByIdAsync(SourceId.Create(SecFilingsBatchRunner.Source));

        var (runner, context) = await SecFilingsBatchDoor.OpenAsync(
            services, batch, authorization, source, Environment.GetEnvironmentVariable, attempt);

        var outcome = await runner.RunAsync(context);

        // The immutable record, written whatever happened - a refusal is as much a fact as a fetch.
        await Universe.WriteAsync(
            SecFilingsBatchDoor.ArtefactPathFor(batch.Index, attempt),
            outcome.ToArtefact(SecFilingsBatchDoor.SealedFingerprint, authorization));

        _output.WriteLine(Universe.Inv($"batch {batch.Index} ({batch.Cik} / {batch.Symbol}) attempt {attempt}: {outcome.Status}. Consumed this run {outcome.ConsumedThisRun}, total {outcome.ConsumedTotal} of {authorization.DispatchCeiling}, remaining {outcome.Remaining}."));

        // Whatever the provider did, these hold.
        Assert.True(outcome.ConsumedThisRun <= batch.Count);
        Assert.True(authorization.Consumed <= authorization.DispatchCeiling);
        Assert.Equal(outcome.Dispatched ? 1 : 0, outcome.ConsumedThisRun);
        Assert.False(outcome.RequestCarriedWindow);
    }

    /// <summary>How many attempts at this batch have already been recorded, plus one.</summary>
    private static async Task<int> AttemptNumberAsync(int batchIndex)
    {
        var directory = Universe.RepositoryPath("artifacts", "universe");

        if (!Directory.Exists(directory))
        {
            return 1;
        }

        var mine = Universe.Inv($"acquisition-sec-{batchIndex:00}-attempt-");
        var attempts = 0;

        foreach (var path in Directory.EnumerateFiles(directory, SecFilingsBatchDoor.ArtefactPattern))
        {
            if (Path.GetFileName(path).StartsWith(mine, StringComparison.Ordinal))
            {
                attempts++;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);

        return attempts + 1;
    }

    // ================= 1. the wiring resolves the runner from the real container =================

    /// <summary>
    /// Every dependency the runner needs comes out of the host the application actually composes.
    /// </summary>
    [Fact]
    public void The_runtime_wiring_resolves_the_sec_runner_from_the_real_container()
    {
        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;

        var runner = new SecFilingsBatchRunner(
            services.GetRequiredService<IDataAcquisition>(),
            services.GetRequiredService<IIngestionRunStore>(),
            services.GetRequiredService<IRawResponseArchive>(),
            services.GetRequiredService<IClock>());

        Assert.NotNull(runner);

        // And the request it would build is the one the connector accepts: no window, CIK subject.
        var request = runner.BuildRequest(Ciks[0], SecFilingsBatchRunner.CorrelationFor(1, 1, Ciks[0]));

        Assert.Null(request.Window);
        Assert.Equal(DataCategory.RegulatoryFilings, request.Category);
        Assert.Equal(SecFilingsBatchRunner.Source, request.SourceId.Value);
        Assert.Equal(Ciks[0], request.Subject.Identifier);
    }

    // ================= 2, 3, 10. the connector switch =================

    /// <summary>
    /// The connector is not registered, because configuration says so - and that is the whole switch.
    /// </summary>
    /// <remarks>
    /// <c>AddSecEdgar</c> reads <c>Providers:SecEdgar:Enabled</c> while the container is being built
    /// and registers nothing when it is false. So the catalogue - the real one, from the real host -
    /// has no EDGAR connector, and the preflight refuses under the gateway's own rule. There is no
    /// separate "is it enabled" question, and no second rule for the same condition.
    /// </remarks>
    [Fact]
    public async Task A_disabled_connector_is_absent_from_the_real_catalogue_and_nothing_can_leave()
    {
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("src", "AI.Investment.Api", "appsettings.json")));

        Assert.False(settings.RootElement
            .GetProperty("Providers").GetProperty("SecEdgar").GetProperty("Enabled").GetBoolean());

        using var scope = _factory.Services.CreateScope();

        var catalogue = scope.ServiceProvider.GetRequiredService<IProviderCatalogue>();

        // The switch, read from the container the application builds for itself.
        Assert.Null(catalogue.Find(SecEdgarProvider.Id));

        Assert.DoesNotContain(catalogue.All(), p => p.SourceId == SecEdgarProvider.Id);
    }

    /// <summary>
    /// With the connector disabled, an otherwise perfect run is refused and spends nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The batch is authorised <em>in this test's own copy only</em> - a local <c>with</c>
    /// expression, never the partition - so that the gate under test is the connector rather than
    /// the approval. Authorising a batch in the repository is a separate act this phase did not
    /// perform, and the fact below asserts it was not.
    /// </para>
    /// <para>
    /// The provider is not stubbed in. It is taken from the real catalogue, where configuration left
    /// it absent, which is the difference between testing the wiring and testing a fake.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task With_the_connector_disabled_the_real_wiring_refuses_and_consumes_nothing()
    {
        using var scope = _factory.Services.CreateScope();

        var authorization = SecFilingsBatchDoor.LoadAuthorization();

        Assert.Equal(0, authorization.Consumed);

        var authorisedCopy = SecEdgarSixMemberPartition.Batches[0] with { Authorised = true };

        var (runner, context) = await SecFilingsBatchDoor.OpenAsync(
            scope.ServiceProvider,
            authorisedCopy,
            authorization,
            InMemorySource(),
            _ => "someone@example.com",
            attemptNumber: 1);

        // The connector the real container handed back.
        Assert.Null(context.Provider);

        var outcome = await runner.RunAsync(context);

        Assert.Equal(SecFilingsBatchRunner.RefusedStatus, outcome.Status);
        Assert.Equal(IngestionGateway.ProviderAvailableRule, outcome.RuleId);
        Assert.False(outcome.Dispatched);
        Assert.Equal(0, outcome.ConsumedThisRun);
        Assert.Equal(0, authorization.Consumed);
        Assert.Equal(6, authorization.Remaining);
    }

    // ================= 4, 5. the approval switch =================

    /// <summary>
    /// With no batch approved, the real wiring refuses at the first gate and spends nothing.
    /// </summary>
    /// <remarks>
    /// The partition as it actually stands, not a copy. This is the lock that stands even if the
    /// connector were switched on, and it is checked before a single service is used.
    /// </remarks>
    [Fact]
    public async Task With_no_batch_approved_the_real_wiring_refuses_and_consumes_nothing()
    {
        using var scope = _factory.Services.CreateScope();

        var authorization = SecFilingsBatchDoor.LoadAuthorization();

        foreach (var batch in SecEdgarSixMemberPartition.Batches)
        {
            Assert.False(batch.Authorised);

            var (runner, context) = await SecFilingsBatchDoor.OpenAsync(
                scope.ServiceProvider,
                batch,
                authorization,
                InMemorySource(),
                _ => "someone@example.com",
                attemptNumber: 1);

            var outcome = await runner.RunAsync(context);

            Assert.Equal(SecFilingsBatchRunner.BatchAuthorisedRule, outcome.RuleId);
            Assert.False(outcome.Dispatched);
            Assert.Equal(0, outcome.ConsumedThisRun);
        }

        Assert.Equal(0, authorization.Consumed);
        Assert.Equal(6, authorization.Remaining);
    }

    // ================= 6. the authorisation being resolved =================

    /// <summary>
    /// The door resolves the installed authorisation, and the spending it reads is what has run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This fact has been rebased six times, once per batch, and every one is recorded
    /// here rather than made quietly.</strong> It originally asserted that nothing had been charged
    /// - true when it was written, and a statement about a moment rather than about an invariant.
    /// The QUMU canary spent the first unit, then LGIQ the second, ONEM the third, NGM the fourth,
    /// SDC the fifth and SHPW the sixth, so it is rebased onto six. Each time the assertion failed
    /// on the next run, correctly reporting the accounting it was written to police.
    /// </para>
    /// <para>
    /// <c>SplitBatchThreeReadinessTests</c> hit the same thing and records the same resolution: a
    /// fact that pins a next step stops holding when the step is taken, and rebasing it onto the
    /// state that now stands is a change of contract worth writing down. What is asserted now is the
    /// accounting itself - six units spent, by six named attempts in the order they ran, leaving
    /// none - which is checkable against the artefacts. This is the last rebase: the ceiling is
    /// reached, the partition is closed, and there is no further batch whose approval could move
    /// the number again.
    /// </para>
    /// <para>
    /// The digest, ceiling, satisfied count and subject list are unchanged by any of it, and are
    /// asserted as literals for exactly that reason.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_authorisation_the_door_resolves_is_the_installed_one()
    {
        var authorization = SecFilingsBatchDoor.LoadAuthorization();

        Assert.Equal(AuthorizationId, authorization.AuthorizationId);
        Assert.Equal(ApprovedDigest, authorization.Digest);
        Assert.Equal(6, authorization.DispatchCeiling);
        Assert.Equal(0, authorization.AlreadySatisfied);
        Assert.Null(authorization.SupersedesAuthorizationId);

        Assert.Equal(
            string.Join(",", Ciks),
            string.Join(",", authorization.Symbols.Order(StringComparer.Ordinal)));

        // What has been charged, read from the artefacts rather than assumed. All six have gone.
        var spent = await SecFilingsBatchDoor.PriorConsumptionAsync(authorization.AuthorizationId);

        Assert.Equal(6, spent);

        // By exactly six attempts, one per member, in partition order. One unit each.
        var burned = (await SecFilingsBatchDoor.BurnedCorrelationsAsync()).Order(StringComparer.Ordinal);

        Assert.Equal(
            string.Join(",", Enumerable.Range(1, 6).Select(i => SecFilingsBatchRunner.CorrelationFor(i, 1, Ciks[i - 1]))),
            string.Join(",", burned));

        // And charged through the production counter rather than asserted as arithmetic.
        authorization.RecordPriorConsumption(spent);

        Assert.Equal(6, authorization.Consumed);
        Assert.Equal(0, authorization.Remaining);

        // The ceiling is reached, and no batch declares a prior consumption of six - so the
        // ordering gate now refuses every one of them before the ceiling is even reached for.
        Assert.DoesNotContain(SecEdgarSixMemberPartition.Batches, b => b.ExpectedPriorConsumption == spent);
    }

    // ================= 7, 8. the partition the door resolves =================

    [Fact]
    public void The_door_resolves_exactly_six_one_member_batches_in_a_deterministic_order()
    {
        var indices = new List<int>();
        var ciks = new List<string>();

        for (var index = 1; index <= 6; index++)
        {
            var batch = SecFilingsBatchDoor.BatchAt(index);

            Assert.True(batch is not null, Universe.Inv($"the door resolves no batch {index}"));
            Assert.True(batch!.Count == 1, Universe.Inv($"batch {index} holds {batch.Count} members, not one"));
            Assert.Equal(SecEdgarSixMemberPartition.Declaration, batch.Declaration);

            indices.Add(batch.Index);
            ciks.Add(batch.Cik);
        }

        // Ascending, contiguous, and in the declaration's own ordinal CIK order.
        Assert.Equal([1, 2, 3, 4, 5, 6], indices);
        Assert.Equal(string.Join(",", Ciks), string.Join(",", ciks));

        // Asked twice, the same answer: resolution is a lookup, not a scan of anything that moves.
        Assert.Equal(
            string.Join(",", ciks),
            string.Join(",", Enumerable.Range(1, 6).Select(i => SecFilingsBatchDoor.BatchAt(i)!.Cik)));

        // And there is no seventh.
        Assert.Null(SecFilingsBatchDoor.BatchAt(0));
        Assert.Null(SecFilingsBatchDoor.BatchAt(7));
    }

    /// <summary>The two gate variables have no defaults, so nothing runs by accident.</summary>
    [Fact]
    public void Neither_gate_variable_has_a_default()
    {
        Assert.False(SecFilingsBatchDoor.IsOpen(_ => null));
        Assert.False(SecFilingsBatchDoor.IsOpen(_ => string.Empty));
        Assert.False(SecFilingsBatchDoor.IsOpen(_ => "true"));
        Assert.False(SecFilingsBatchDoor.IsOpen(_ => "yes"));
        Assert.True(SecFilingsBatchDoor.IsOpen(_ => "1"));

        Assert.Null(SecFilingsBatchDoor.Batch(_ => null));
        Assert.Null(SecFilingsBatchDoor.Batch(_ => string.Empty));
        Assert.Null(SecFilingsBatchDoor.Batch(_ => "first"));
        Assert.Equal(1, SecFilingsBatchDoor.Batch(_ => "1")!.Index);

        // And the door is shut in this process, which is why the gated fact above is skipped.
        Assert.False(SecFilingsBatchDoor.IsOpen(Environment.GetEnvironmentVariable));
    }

    // ================= 9. the contact, required and never exposed =================

    /// <summary>
    /// The contact is required and fails closed, and nothing that persists ever carries its value.
    /// </summary>
    /// <remarks>
    /// Two halves. The wiring refuses when the address is absent or malformed - checked through the
    /// same full validation the connector's options use, so a caller checking only the blank rule
    /// cannot let <c>not-an-email</c> through. And the refusal names the variable rather than the
    /// value, so the one artefact a run writes cannot carry an address into a file that outlives it.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public async Task The_wiring_fails_closed_without_a_usable_contact_and_never_prints_it(string? contact)
    {
        const string address = "a.real.person@example.org";

        using var scope = _factory.Services.CreateScope();

        var authorization = SecFilingsBatchDoor.LoadAuthorization();

        // Authorised in this copy only, so the gate under test is the contact rather than the
        // approval; and the provider is supplied so the preflight is not the refusing gate either.
        var (runner, context) = await SecFilingsBatchDoor.OpenAsync(
            scope.ServiceProvider,
            SecEdgarSixMemberPartition.Batches[0] with { Authorised = true },
            authorization,
            InMemorySource(),
            _ => contact,
            attemptNumber: 1);

        var outcome = await runner.RunAsync(context with { Provider = new PresentProvider() });

        Assert.Equal(SecFilingsBatchRunner.DeploymentIdentityRule, outcome.RuleId);
        Assert.Equal(0, outcome.ConsumedThisRun);
        Assert.Equal(0, authorization.Consumed);

        // Names the variable, never the value - not the one supplied, and not a plausible one.
        Assert.Contains(SecFilingsBatchRunner.ContactVariable, outcome.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("@", outcome.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(address, outcome.Reason!, StringComparison.Ordinal);

        // And the artefact a run would write carries no address either. Not asserted as "no @",
        // because the evidence-base fingerprint and every versioned rule identifier legitimately
        // contain one; asserted as the addresses themselves, which must appear nowhere.
        var artefact = outcome.ToArtefact(SecFilingsBatchDoor.SealedFingerprint, authorization);

        Assert.DoesNotContain(address, artefact, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", artefact, StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(contact))
        {
            Assert.DoesNotContain(contact, artefact, StringComparison.Ordinal);
            Assert.DoesNotContain(contact, outcome.Reason!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_application_name_is_the_established_one_and_is_not_invented_here() =>
        Assert.Equal("AI-Investment-Analyst", SecFilingsBatchRunner.ApplicationName);

    // ================= 11. the EODHD paths are untouched =================

    /// <summary>
    /// The split runner's own door is exactly as it was: its partition, its declaration, its gates.
    /// </summary>
    [Fact]
    public void The_existing_eodhd_execution_paths_are_unchanged()
    {
        // Three batches, none approved, all naming the split declaration.
        Assert.Equal([1, 2, 3], AcquisitionSplitBatchTests.Partition.Select(b => b.Index).ToList());
        Assert.DoesNotContain(AcquisitionSplitBatchTests.Partition, b => b.Authorised);

        Assert.All(
            AcquisitionSplitBatchTests.Partition,
            b => Assert.Equal("acquisition-eodhd-splits-final-2021-09-to-2026-08.json", b.Declaration));

        // No split batch names the SEC declaration, and no SEC batch names an EODHD one.
        Assert.DoesNotContain(
            AcquisitionSplitBatchTests.Partition,
            b => b.Declaration.Contains("sec-edgar", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            SecEdgarSixMemberPartition.Batches,
            b => b.Declaration.Contains("eodhd", StringComparison.OrdinalIgnoreCase));

        // The two doors use different variables, so opening one cannot open the other.
        Assert.NotEqual("AIINV_SPLIT_BATCH", SecFilingsBatchDoor.GateVariable);
        Assert.NotEqual("AIINV_SPLIT_BATCH_INDEX", SecFilingsBatchDoor.IndexVariable);
        Assert.NotEqual("AIINV_ACQUISITION_BATCH", SecFilingsBatchDoor.GateVariable);
    }

    // ================= the scope the door reads =================

    /// <summary>
    /// The reading scope comes from the declaration, and the secondary forms stay with one member.
    /// </summary>
    [Fact]
    public async Task The_door_reads_the_declarations_own_scope_and_keeps_it_member_bound()
    {
        var scope = await SecFilingsBatchDoor.ReadScopeAsync();

        Assert.Equal("0001335112", scope.SecondaryFormsCik);
        Assert.Equal("424B*,S-1,S-3", string.Join(",", scope.SecondaryForms));
        Assert.Contains("8-K", scope.SharedForms);
        Assert.Contains("25-NSE", scope.SharedForms);

        // Every member the declaration states a breach for has at least one selection window, and
        // every window start is inside the authorisation's own scope.
        foreach (var cik in Ciks)
        {
            var windows = scope.WindowsFor(cik);

            Assert.NotEmpty(windows);

            var earliest = scope.EarliestWindowStartFor(cik);

            Assert.InRange(earliest, SecFilingsBatchRunner.WindowFrom, SecFilingsBatchRunner.WindowTo);
            Assert.Equal(windows.Min(w => w.From), earliest);
        }

        // SHPW carries fifteen breaches and QUMU one; the door does not flatten them.
        Assert.True(scope.WindowsFor("0001784851").Count > scope.WindowsFor("0000892482").Count);

        // A member with no stated window fails closed rather than being treated as fully covered.
        Assert.Equal(DateOnly.MaxValue, scope.EarliestWindowStartFor("0000320193"));
    }

    // ================= helpers =================

    /// <summary>
    /// The EDGAR registry entry, built in memory so these facts need no database.
    /// </summary>
    /// <remarks>
    /// The source row is the one thing the door reads from the store, and reading it requires a
    /// database that a normal test run may not have. What these facts are about is the connector
    /// switch, which is configuration rather than data - so the source is supplied and the provider
    /// is taken from the real catalogue, which is where the switch actually lands.
    /// </remarks>
    private static DataSource InMemorySource()
    {
        var registered = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var source = DataSource.Register(
            SecEdgarProvider.Id,
            "U.S. Securities and Exchange Commission - EDGAR",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            [DataCategory.RegulatoryFilings, DataCategory.CompanyProfile],
            UpdateCadence.EventDriven,
            LicensingTerms.Create(
                storageAllowed: true,
                redistributionAllowed: true,
                automatedProcessingAllowed: true,
                attributionRequired: true,
                notes: SecEdgarSource.LicensingNotes,
                retention: RetentionLimit.Unlimited),
            VerificationPolicy.Authoritative,
            registered,
            "Built in memory for the wiring facts.");

        source.Activate(registered);

        return source;
    }

    /// <summary>A connector that declares what EDGAR declares and can fetch nothing.</summary>
    private sealed class PresentProvider : IDataProvider
    {
        public SourceId SourceId => SecEdgarProvider.Id;

        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Create(
            [DataCategory.RegulatoryFilings, DataCategory.CompanyProfile],
            [Region.UnitedStates],
            ["Company"],
            supportsWindow: false,
            maxWindowDuration: null,
            quota: ProviderQuota.PerSecond(10));

        public Task<ProviderResponse> FetchAsync(
            IngestionRequest request,
            string? continuationToken = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Nothing in these tests may reach a provider. Reaching this line means a gate was skipped.");
    }
}
