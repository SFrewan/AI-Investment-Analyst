using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// No authorisation unit is spent on a request the deterministic gates would have refused.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hermetic, and nothing here can reach a provider.</strong> The connector used throughout
/// is a stub whose <c>FetchAsync</c> throws, the source is built in memory, and the authorisation is
/// a synthetic declaration written to a temporary file and deleted afterwards. The installed SEC
/// authorisation is loaded read-only in one fact, to prove it is untouched.
/// </para>
/// <para>
/// <strong>What was wrong.</strong> A runner charges its authorisation at intent - one unit claimed
/// immediately before the request is handed to <c>IDataAcquisition</c>, never credited back - but
/// four gates stand inside <c>IngestionGateway.IngestAsync</c>, on the far side of that charge. A
/// request refused for registration, admission or capability therefore spent a unit for a request
/// that never left the process. With SEC EDGAR disabled, its source possibly inactive, and a request
/// shape never exercised for this category, the three likeliest first-run outcomes were all in that
/// band: six units gone, nothing sent.
/// </para>
/// <para>
/// <strong>What changed, and what deliberately did not.</strong>
/// <see cref="IngestionPreflight"/> evaluates the same decisions, in the same order, under the same
/// rule identifiers, over values a caller has already read - purely, so asking costs nothing. The
/// gateway is untouched and still runs all four gates on the way to the provider. One gate stays
/// behind and is named rather than hidden: see
/// <see cref="The_rate_limit_gate_cannot_be_preflighted_and_that_is_recorded_rather_than_hidden"/>.
/// </para>
/// </remarks>
public sealed class SecEdgarPreDispatchConsumptionTests : IClassFixture<UniverseApiFactory>
{
    private const string Source = "sec-edgar";
    private const string Evidence = "phase-a-synthetic-evidence-base@0000";

    private const string InstalledDeclaration =
        "acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string ApprovedDigest =
        "499f211f9c931c78d7faf1beb76eef3205fec12cb28db06eeeda461964567682";

    private static readonly string[] Ciks =
    [
        "0000892482", "0001335112", "0001404123", "0001426332", "0001775625", "0001784851",
    ];

    private static readonly DateOnly From = new(2021, 9, 1);
    private static readonly DateOnly To = new(2026, 8, 31);

    private static readonly DateTime Registered = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly UniverseApiFactory _factory;

    public SecEdgarPreDispatchConsumptionTests(UniverseApiFactory factory) => _factory = factory;

    // ---------- the filings normaliser is discoverable ----------

    /// <summary>
    /// The container registers the filings normaliser, and it is the one the pipeline would find.
    /// </summary>
    /// <remarks>
    /// <c>NormalizationPipeline.FindNormalizer</c> walks the registered
    /// <c>IEnumerable&lt;INormalizer&gt;</c> and takes the first whose <c>CanNormalize</c> is true.
    /// Asking the real container the same question is what proves discoverability rather than mere
    /// existence: before this registration, the walk found nothing and every filings payload was
    /// quarantined under <c>normalization.no-normalizer@1</c> with the bytes intact and no
    /// observation produced.
    /// </remarks>
    [Fact]
    public void The_filings_normaliser_is_registered_and_is_the_one_the_pipeline_would_find()
    {
        using var scope = _factory.Services.CreateScope();

        var normalizers = scope.ServiceProvider.GetServices<INormalizer>().ToList();

        Assert.NotEmpty(normalizers);

        var claiming = normalizers
            .Where(n => n.CanNormalize(SecEdgarProvider.Id, DataCategory.RegulatoryFilings))
            .ToList();

        // Exactly one claims it, so the pipeline's first match is unambiguous.
        Assert.Single(claiming);
        Assert.IsType<SecEdgarFilingsNormalizer>(claiming[0]);

        // And the categories the other EDGAR normalisers claim are unchanged by its arrival.
        Assert.Single(normalizers, n => n.CanNormalize(SecEdgarProvider.Id, DataCategory.CompanyProfile));
        Assert.Single(normalizers, n => n.CanNormalize(SecEdgarProvider.Id, DataCategory.FinancialStatements));
        Assert.Single(normalizers, n => n.CanNormalize(SecEdgarProvider.Id, DataCategory.MarketWideDisclosure));
    }

    // ---------- refusals that must cost nothing ----------

    [Fact]
    public void A_disabled_connector_is_refused_before_a_unit_is_consumed() =>
        AssertRefusedWithoutConsuming(
            ActiveSecSource(),
            provider: null,
            SecRequest(),
            IngestionGateway.ProviderAvailableRule);

    [Fact]
    public void An_unregistered_source_is_refused_before_a_unit_is_consumed() =>
        AssertRefusedWithoutConsuming(
            source: null,
            SecProvider(),
            SecRequest(),
            IngestionGateway.SourceRegisteredRule);

    [Fact]
    public void An_inactive_source_is_refused_before_a_unit_is_consumed() =>
        AssertRefusedWithoutConsuming(
            SecSource(active: false),
            SecProvider(),
            SecRequest(),
            SourceAdmission.SourceActiveRule);

    /// <summary>
    /// A request carrying a window is refused before a unit is consumed.
    /// </summary>
    /// <remarks>
    /// The specific mistake this exists for. <c>SecEdgarProvider</c> declares
    /// <c>supportsWindow: false</c> because the submissions endpoint returns one whole document and
    /// takes no period. The authorisation, meanwhile, is scoped to 2021-09-01..2026-08-31 and
    /// <c>Covers</c> requires exactly those dates as arguments. Two different windows: one a scope
    /// check on constants, one a transport parameter that must be absent. Conflating them used to
    /// cost a unit - charged at intent, then refused at the capability gate one step later.
    /// </remarks>
    [Fact]
    public void A_windowed_request_is_refused_before_a_unit_is_consumed() =>
        AssertRefusedWithoutConsuming(
            ActiveSecSource(),
            SecProvider(),
            SecRequest(window: DateRange.Create(
                new DateTime(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc))),
            ProviderCapabilityCheck.WindowSupportedRule);

    [Fact]
    public void A_subject_kind_the_connector_does_not_understand_is_refused_before_consumption() =>
        AssertRefusedWithoutConsuming(
            ActiveSecSource(),
            SecProvider(),
            SecRequest(subjectKind: "Security", identifier: "QUMU.US"),
            ProviderCapabilityCheck.SubjectKindSupportedRule);

    [Fact]
    public void A_category_the_source_does_not_supply_is_refused_before_consumption() =>
        AssertRefusedWithoutConsuming(
            ActiveSecSource(),
            SecProvider(),
            SecRequest(category: DataCategory.MarketPrices),
            SourceAdmission.SuppliesCategoryRule);

    // ---------- the one dispatch that should cost exactly one ----------

    /// <summary>
    /// An admitted request consumes exactly one unit, and only after every deterministic gate passed.
    /// </summary>
    /// <remarks>
    /// The ordering is the assertion. The counter is read before the preflight, after the preflight
    /// and after the consumption, and it moves exactly once, at the step that is supposed to move it.
    /// </remarks>
    [Fact]
    public void An_admitted_request_consumes_exactly_one_unit_and_not_before()
    {
        WithSyntheticAuthorization(authorization =>
        {
            var request = SecRequest();

            Assert.Equal(0, authorization.Consumed);

            var preflight = IngestionPreflight.Evaluate(ActiveSecSource(), SecProvider(), request);

            Assert.True(preflight.IsAdmitted, preflight.Reason);
            Assert.Null(preflight.RuleId);

            // Still nothing spent. The preflight is pure: it reserves nothing and charges nothing.
            Assert.Equal(0, authorization.Consumed);

            var permitted = authorization.TryConsume(Source, Ciks[0], From, To);

            Assert.True(permitted.Allowed, permitted.Reason);
            Assert.Equal(1, authorization.Consumed);
            Assert.Equal(5, authorization.Remaining);

            // And a second member costs a second unit, not the same one twice.
            Assert.True(authorization.TryConsume(Source, Ciks[1], From, To).Allowed);
            Assert.Equal(2, authorization.Consumed);
        });
    }

    /// <summary>An unauthorised batch never reaches the preflight, let alone consumption.</summary>
    /// <remarks>
    /// The partition's own flag is the first gate a runner checks, ahead of every service it
    /// resolves. Nothing here is authorised, so the counter cannot move: the assertion is that the
    /// flag is false for every batch and that skipping the loop leaves the counter where it was.
    /// </remarks>
    [Fact]
    public void An_unauthorised_batch_consumes_nothing()
    {
        WithSyntheticAuthorization(authorization =>
        {
            foreach (var batch in SecEdgarSixMemberPartition.Batches)
            {
                if (!batch.Authorised)
                {
                    continue;
                }

                // Unreachable while the partition is at rest. Written as the runner writes it so
                // that flipping a flag without an approval makes this fail rather than pass quietly.
                _ = authorization.TryConsume(Source, batch.Cik, From, To);
            }

            Assert.Equal(0, authorization.Consumed);
            Assert.Equal(6, authorization.Remaining);
        });
    }

    // ---------- the two windows stay apart ----------

    [Fact]
    public void Covers_uses_the_full_authorisation_window_while_the_request_carries_none()
    {
        WithSyntheticAuthorization(authorization =>
        {
            // The authorisation's scope check takes the full window as literal arguments.
            Assert.Equal(From, authorization.WindowFrom);
            Assert.Equal(To, authorization.WindowTo);
            Assert.True(authorization.Covers(Source, Ciks[0], From, To).Allowed);

            // Any other window is out of scope, so the authorisation cannot be quietly narrowed.
            Assert.False(authorization.Covers(Source, Ciks[0], From, new DateOnly(2026, 8, 30)).Allowed);
            Assert.False(authorization.Covers(Source, Ciks[0], new DateOnly(2021, 9, 2), To).Allowed);

            // And the request that would actually be dispatched carries no window at all.
            var request = SecRequest();

            Assert.Null(request.Window);

            // Which is what the connector's own declaration requires.
            Assert.False(SecProvider().Capabilities.SupportsWindow);

            Assert.True(
                IngestionPreflight.Evaluate(ActiveSecSource(), SecProvider(), request).IsAdmitted);
        });
    }

    // ---------- the gate that could not be hoisted, named rather than hidden ----------

    /// <summary>
    /// The rate limiter reserves when it passes, so it stays inside the gateway - and that is stated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IProviderRateLimiter.TryAcquireAsync</c> is the only gate in front of a fetch that has an
    /// effect: passing it <em>reserves</em> a request against the declared quota. Evaluating it in a
    /// preflight would spend a slot that the gateway would then spend again, so the requirement
    /// "a rate-limit refusal consumes zero authorisation units" <strong>is not met</strong> and
    /// cannot be met by reordering alone. A run refused on rate limit still costs one unit.
    /// </para>
    /// <para>
    /// This is recorded here rather than worked around because the alternatives are both worse:
    /// duplicating the reservation trades an authorisation error for a quota error, and adding a
    /// non-reserving peek to the interface would be a new production concept every implementation
    /// must satisfy - which Phase A was told not to introduce. At one paced request per batch the
    /// quota cannot be exhausted, so the exposure is real and unreachable, which is exactly the kind
    /// of thing that should be written down rather than assumed away.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_rate_limit_gate_cannot_be_preflighted_and_that_is_recorded_rather_than_hidden()
    {
        Assert.Contains(IngestionGateway.WithinRateLimitRule, IngestionPreflight.DeferredRules);
        Assert.DoesNotContain(IngestionGateway.WithinRateLimitRule, IngestionPreflight.PreflightedRules);

        // The seam is deferred too, for a different reason: it is external state and it audits.
        Assert.Contains(IngestionGateway.PolicyPermittedRule, IngestionPreflight.DeferredRules);

        // Exactly these two, so a third gate cannot be quietly moved out of the preflight later.
        Assert.Equal(
            $"{IngestionGateway.PolicyPermittedRule},{IngestionGateway.WithinRateLimitRule}",
            string.Join(",", IngestionPreflight.DeferredRules.Order(StringComparer.Ordinal)));
    }

    // ---------- no gate weakened, none invented ----------

    /// <summary>
    /// Every gate the gateway declares is either preflighted or explicitly deferred - none dropped.
    /// </summary>
    [Fact]
    public void Every_gateway_gate_is_either_preflighted_or_explicitly_deferred()
    {
        string[] gateway =
        [
            IngestionGateway.SourceRegisteredRule,
            IngestionGateway.ProviderAvailableRule,
            IngestionGateway.WithinRateLimitRule,
            IngestionGateway.PolicyPermittedRule,
        ];

        var accounted = IngestionPreflight.PreflightedRules
            .Concat(IngestionPreflight.DeferredRules)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(gateway, rule => Assert.True(
            accounted.Contains(rule),
            Universe.Inv($"`{rule}` is a gateway gate that the preflight neither evaluates nor records as deferred")));
    }

    /// <summary>
    /// The preflight invents no rule of its own: every identifier it can return already existed.
    /// </summary>
    /// <remarks>
    /// A new rule identifier would mean a new decision, and a new decision is a gate this review did
    /// not authorise. Every rule below is a constant declared by the gateway, source admission or the
    /// capability check - the same names, so a refusal reads identically wherever it happened.
    /// </remarks>
    [Fact]
    public void The_preflight_invents_no_rule_of_its_own()
    {
        var existing = new HashSet<string>(StringComparer.Ordinal)
        {
            IngestionGateway.SourceRegisteredRule,
            IngestionGateway.ProviderAvailableRule,
            SourceAdmission.SourceActiveRule,
            SourceAdmission.CategoryRecognisedRule,
            SourceAdmission.SuppliesCategoryRule,
            SourceAdmission.StoragePermittedRule,
            SourceAdmission.ProcessingPermittedRule,
            ProviderCapabilityCheck.CategorySupportedRule,
            ProviderCapabilityCheck.RegionSupportedRule,
            ProviderCapabilityCheck.SubjectKindSupportedRule,
            ProviderCapabilityCheck.WindowSupportedRule,
            ProviderCapabilityCheck.WindowWithinLimitRule,
        };

        Assert.All(IngestionPreflight.PreflightedRules, rule => Assert.True(
            existing.Contains(rule),
            Universe.Inv($"`{rule}` is not a rule any existing gate declares")));
    }

    // ---------- deployment identity ----------

    /// <summary>
    /// An absent or malformed contact address is refused by the options the connector already has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are needed and only one is obvious. <c>Validate</c> catches a blank address;
    /// the <c>[EmailAddress]</c> annotation catches a malformed one, and annotations only run when
    /// the validator is asked for them with <c>validateAllProperties</c>. A caller checking
    /// <c>Validate</c> alone would accept <c>not-an-email</c>, so the full validation is what any
    /// runner must use - and must use before the host is built, since options validation otherwise
    /// fires only when the container is constructed.
    /// </para>
    /// <para>
    /// <c>EmailAddressAttribute</c> is permissive about what follows the <c>@</c>, so this is a
    /// floor rather than a guarantee that the address is monitored. Nothing can check the latter,
    /// which is why the address is deployment configuration a person supplies rather than a value
    /// this repository holds.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-an-email", false)]
    [InlineData("someone@example.com", true)]
    public void The_sec_contact_address_is_validated_before_anything_could_use_it(
        string contact,
        bool expected)
    {
        var options = new SecEdgarOptions
        {
            Enabled = true,
            ApplicationName = "AI-Investment-Analyst",
            ContactEmail = contact,
        };

        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);

        Assert.Equal(expected, valid);

        // Whatever it decides, the value itself never reaches a message this test could print.
        Assert.DoesNotContain(results, r => (r.ErrorMessage ?? string.Empty).Contains(contact, StringComparison.Ordinal)
            && contact.Length > 0);
    }

    [Fact]
    public void A_disabled_connector_needs_no_contact_and_still_supplies_none()
    {
        var options = new SecEdgarOptions();

        Assert.False(options.Enabled);
        Assert.Empty(options.ContactEmail);
        Assert.Empty(options.Validate(new ValidationContext(options)));
    }

    // ---------- the installed authorisation is untouched by any of this ----------

    [Fact]
    public void The_installed_authorisation_is_read_only_here_and_still_spends_nothing()
    {
        var installed = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", InstalledDeclaration),
            SealedFingerprint);

        Assert.Equal(ApprovedDigest, installed.Digest);
        Assert.Equal(0, installed.Consumed);
        Assert.Equal(6, installed.Remaining);
        Assert.Equal(6, installed.DispatchCeiling);

        Assert.DoesNotContain(SecEdgarSixMemberPartition.Batches, b => b.Authorised);
    }

    // ---------- helpers ----------

    private static void AssertRefusedWithoutConsuming(
        DataSource? source,
        IDataProvider? provider,
        IngestionRequest request,
        string expectedRule)
    {
        WithSyntheticAuthorization(authorization =>
        {
            var preflight = IngestionPreflight.Evaluate(source, provider, request);

            Assert.False(
                preflight.IsAdmitted,
                Universe.Inv($"the preflight admitted a request it should have refused under `{expectedRule}`"));

            Assert.Equal(expectedRule, preflight.RuleId);
            Assert.False(string.IsNullOrWhiteSpace(preflight.Reason));

            // The point of the whole change: a refusal here costs nothing, because nothing was
            // charged before it. A runner that consulted the preflight first cannot reach
            // TryConsume for a request the gateway would have refused anyway.
            Assert.Equal(0, authorization.Consumed);
            Assert.Equal(6, authorization.Remaining);
        });
    }

    /// <summary>
    /// Runs an assertion against a synthetic authorisation written to a temporary file.
    /// </summary>
    /// <remarks>
    /// Synthetic on purpose. Consumption mutates an in-memory counter, and although nothing persists
    /// it without an outcome artefact, exercising it against the installed declaration's object would
    /// blur the line these tests exist to keep sharp. This one names a made-up evidence base, lives
    /// in the temp directory, and is deleted whatever the assertion does.
    /// </remarks>
    private static void WithSyntheticAuthorization(Action<AcquisitionAuthorization> assertion)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            string.Create(CultureInfo.InvariantCulture, $"phase-a-{Guid.NewGuid():N}.json"));

        try
        {
            File.WriteAllText(path, SyntheticDeclaration());

            assertion(AcquisitionAuthorization.Load(path, Evidence));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The same shape as the installed declaration, with a digest computed the same way.</summary>
    private static string SyntheticDeclaration()
    {
        string[] sources = [Source];

        var digest = AcquisitionAuthorization.DigestFor(
            AcquisitionAuthorization.Schema,
            Evidence,
            "sec",
            sources,
            From,
            To,
            planned: 6,
            satisfied: 0,
            ceiling: 6,
            symbols: Ciks,
            supersedesAuthorizationId: null,
            alreadyConsumed: 0);

        return JsonSerializer.Serialize(new
        {
            Schema = AcquisitionAuthorization.Schema,
            AuthorizationId = "phase-a-synthetic",
            EvidenceBaseFingerprint = Evidence,
            Vendor = "sec",
            Sources = sources,
            WindowFromUtc = "2021-09-01",
            WindowToUtc = "2026-08-31",
            PlannedRequests = 6,
            AlreadySatisfied = 0,
            DispatchCeiling = 6,
            Symbols = Ciks,
            AuthorizationDigest = digest,
        });
    }

    private static IngestionRequest SecRequest(
        DataCategory category = DataCategory.RegulatoryFilings,
        string subjectKind = "Company",
        string? identifier = null,
        DateRange? window = null) =>
        IngestionRequest.Create(
            SourceId.Create(Source),
            category,
            Region.UnitedStates,
            IngestionSubject.Create(subjectKind, identifier ?? Ciks[0]),
            CorrelationId.Create("phase-a-preflight"),
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            window);

    private static DataSource ActiveSecSource() => SecSource(active: true);

    /// <summary>The EDGAR registry entry as <c>SecEdgarSource</c> defines it, built in memory.</summary>
    private static DataSource SecSource(bool active)
    {
        var source = DataSource.Register(
            SourceId.Create(Source),
            "U.S. Securities and Exchange Commission - EDGAR",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            [
                DataCategory.RegulatoryFilings,
                DataCategory.CompanyProfile,
                DataCategory.FinancialStatements,
                DataCategory.EarningsDisclosure,
                DataCategory.MarketWideDisclosure,
            ],
            UpdateCadence.EventDriven,
            LicensingTerms.Create(
                storageAllowed: true,
                redistributionAllowed: true,
                automatedProcessingAllowed: true,
                attributionRequired: true,
                notes: SecEdgarSource.LicensingNotes,
                retention: RetentionLimit.Unlimited),
            VerificationPolicy.Authoritative,
            Registered,
            "Built in memory for this test. Registration records assessment, not permission.");

        if (active)
        {
            source.Activate(Registered);
        }

        return source;
    }

    /// <summary>
    /// A connector declaring exactly what <c>SecEdgarProvider</c> declares, and able to fetch nothing.
    /// </summary>
    private static UnreachableProvider SecProvider() => new();

    private sealed class UnreachableProvider : IDataProvider
    {
        public SourceId SourceId => SecEdgarProvider.Id;

        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Create(
            [
                DataCategory.RegulatoryFilings,
                DataCategory.CompanyProfile,
                DataCategory.FinancialStatements,
                DataCategory.EarningsDisclosure,
                DataCategory.MarketWideDisclosure,
            ],
            [Region.UnitedStates],
            ["Company", "Period"],
            supportsWindow: false,
            maxWindowDuration: null,
            quota: ProviderQuota.PerSecond(10));

        public Task<ProviderResponse> FetchAsync(
            IngestionRequest request,
            string? continuationToken = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Nothing in these tests may reach a provider. Reaching this line means a gate was " +
                "skipped, which is the failure the whole file exists to prevent.");
    }
}
