using System.Text;
using AI.Investment.Application.Freshness;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Sources;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Freshness;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Mapping;

/// <summary>
/// The shapes that cross the application boundary.
/// </summary>
/// <remarks>
/// <para>
/// Mappers look like the least interesting code in a system and are a common place for quiet
/// mistakes, because a wrong field is still a valid response. Three things are asserted here that
/// are not merely mechanical: a licence's permissions survive the crossing individually rather than
/// being collapsed into prose, an absent retention limit stays absent rather than becoming zero,
/// and a run's refusal rule reaches the wire - without it, an operator sees that data did not
/// arrive and not why.
/// </para>
/// <para>
/// One assertion is a safety check rather than a correctness one: a quarantine DTO must not carry
/// anything the quarantine record does not, because that record is designed never to contain an
/// excerpt of a payload.
/// </para>
/// </remarks>
public sealed class DataPlaneMapperTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static DataSource Source(
        LicensingTerms? licensing = null,
        bool active = true,
        UpdateCadence? cadence = null,
        VerificationPolicy? verification = null)
    {
        var source = DataSource.Register(
            SourceId.Create("sec-edgar"),
            "SEC EDGAR",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            [DataCategory.CompanyProfile, DataCategory.RegulatoryFilings],
            cadence ?? UpdateCadence.EventDriven,
            licensing ?? LicensingTerms.OpenData(),
            verification ?? VerificationPolicy.Authoritative,
            Now.AddYears(-1));

        if (active)
        {
            source.Activate(Now.AddYears(-1));
        }

        return source;
    }

    // ---------- sources ----------

    [Fact]
    public void A_source_crosses_the_boundary_intact()
    {
        var dto = SourceMapper.ToDto(Source());

        Assert.Equal("sec-edgar", dto.Id);
        Assert.Equal("SEC EDGAR", dto.Name);
        Assert.Equal("RegulatoryAuthority", dto.Type);
        Assert.Equal("Primary", dto.Authority);
        Assert.Equal(Region.UnitedStates.Code, dto.Region);
        Assert.True(dto.IsActive);
        Assert.Equal("Authoritative", dto.VerificationPolicy);
    }

    [Fact]
    public void Every_category_a_source_supplies_is_reported()
    {
        var dto = SourceMapper.ToDto(Source());

        Assert.Equal(2, dto.Categories.Count);
        Assert.Contains("CompanyProfile", dto.Categories, StringComparer.Ordinal);
        Assert.Contains("RegulatoryFilings", dto.Categories, StringComparer.Ordinal);
    }

    [Fact]
    public void An_inactive_source_is_reported_as_inactive() =>
        Assert.False(SourceMapper.ToDto(Source(active: false)).IsActive);

    [Fact]
    public void Licensing_permissions_cross_individually_rather_than_as_prose()
    {
        // Whether a source may be stored and processed is what an operator needs before activating
        // it. Burying that in a notes field would put a compliance decision behind a
        // reading-comprehension exercise.
        var dto = SourceMapper.ToDto(Source()).Licensing;

        Assert.True(dto.AllowsStorage);
        Assert.True(dto.AllowsAutomatedProcessing);
    }

    [Fact]
    public void An_unlicensed_retention_limit_stays_absent_rather_than_becoming_zero()
    {
        var dto = SourceMapper.ToDto(Source()).Licensing;

        // Null means "the licence sets no limit", which is a stated fact. Zero would mean "delete
        // immediately", which is the opposite.
        Assert.Null(dto.RetentionLimitDays);
    }

    [Fact]
    public void A_licensed_retention_limit_crosses_as_days()
    {
        var bounded = LicensingTerms.Create(
            storageAllowed: true,
            redistributionAllowed: false,
            automatedProcessingAllowed: true,
            attributionRequired: true,
            retention: RetentionLimit.OfDays(365));

        var dto = SourceMapper.ToDto(bounded);

        Assert.Equal(365, dto.RetentionLimitDays);
        Assert.False(dto.AllowsRedistribution);
        Assert.True(dto.RequiresAttribution);
    }

    /// <summary>
    /// The regression this suite was written to catch and did not.
    /// </summary>
    /// <remarks>
    /// <see cref="VerificationPolicy"/> is a value object, not an enum, and its
    /// <c>ToString()</c> is a sentence for a human - "confirms alone". The mapper called
    /// <c>ToString()</c> as if it were an enum, so the wire carried prose that would change
    /// silently the moment anyone reworded it. The assertion below already expected a stable name;
    /// what was missing was any assertion that the structure survived at all.
    /// </remarks>
    [Fact]
    public void A_verification_policy_crosses_as_a_stable_name_and_its_structure()
    {
        var dto = SourceMapper.ToDto(Source());

        Assert.Equal("Authoritative", dto.VerificationPolicy);
        Assert.True(dto.CanConfirmAlone);
        Assert.Equal(1, dto.RequiredIndependentSources);

        // The prose form must not reach the wire under any field.
        Assert.DoesNotContain("confirms alone", dto.VerificationPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void The_other_well_known_verification_policies_have_stable_names()
    {
        Assert.Equal(
            "RequiresCorroboration",
            SourceMapper.ToDto(Source(verification: VerificationPolicy.RequiresCorroboration))
                .VerificationPolicy);

        Assert.Equal(
            "Cautious",
            SourceMapper.ToDto(Source(verification: VerificationPolicy.Cautious)).VerificationPolicy);
    }

    [Fact]
    public void A_policy_that_is_none_of_the_three_is_named_honestly()
    {
        var custom = VerificationPolicy.Create(canConfirmAlone: false, requiredIndependentSources: 4);

        var dto = SourceMapper.ToDto(Source(verification: custom));

        // Forced into the nearest well-known name it would be a lie; the two structural fields
        // carry the actual policy, so "Custom" costs a caller nothing.
        Assert.Equal("Custom", dto.VerificationPolicy);
        Assert.False(dto.CanConfirmAlone);
        Assert.Equal(4, dto.RequiredIndependentSources);
    }

    /// <summary>
    /// The same defect as the verification policy, found by sweeping rather than by a failure.
    /// </summary>
    /// <remarks>
    /// <c>UpdateCadence.ToString()</c> renders "Daily (~1.00:00:00)". A caller wanting the kind
    /// would have had to parse it, and a caller wanting the interval would have had to parse it
    /// worse.
    /// </remarks>
    [Fact]
    public void A_cadence_crosses_as_its_kind_with_the_interval_beside_it()
    {
        var daily = SourceMapper.ToDto(Source(cadence: UpdateCadence.Daily()));

        Assert.Equal("Daily", daily.Cadence);
        Assert.Equal((int)TimeSpan.FromDays(1).TotalSeconds, daily.ExpectedIntervalSeconds);
    }

    [Fact]
    public void A_cadence_that_cannot_be_late_reports_no_interval()
    {
        var eventDriven = SourceMapper.ToDto(Source(cadence: UpdateCadence.EventDriven));

        Assert.Equal("EventDriven", eventDriven.Cadence);

        // Null because it genuinely has none, not because the mapper could not find one.
        Assert.Null(eventDriven.ExpectedIntervalSeconds);
    }

    [Fact]
    public void A_null_source_is_refused() =>
        Assert.Throws<ArgumentNullException>(() => SourceMapper.ToDto((DataSource)null!));

    // ---------- freshness ----------

    [Fact]
    public void A_freshness_line_carries_the_rule_that_reached_it()
    {
        var source = Source();

        var line = new SourceFreshness(
            source.Id,
            source.Name,
            source.Cadence,
            source.IsActive,
            FreshnessPolicy.Assess(source, null, Now));

        var dto = FreshnessMapper.ToDto(line);

        // "Not scheduled" without saying whether that is because it is switched off or because it
        // publishes on events has told an operator half of what they need - and withheld the half
        // they can act on.
        Assert.Equal(FreshnessPolicy.NoExpectedIntervalRule, dto.RuleId);
        Assert.Equal("NotScheduled", dto.State);
    }

    [Fact]
    public void A_source_that_has_never_run_reports_null_elapsed_rather_than_zero()
    {
        var source = Source(cadence: UpdateCadence.Daily());

        var line = new SourceFreshness(
            source.Id,
            source.Name,
            source.Cadence,
            source.IsActive,
            FreshnessPolicy.Assess(source, null, Now));

        var dto = FreshnessMapper.ToDto(line);

        // Zero would mean "refreshed just now", which is the opposite of never having run.
        Assert.Equal("NeverIngested", dto.State);
        Assert.Null(dto.LastRefreshedAtUtc);
        Assert.Null(dto.ElapsedSeconds);
    }

    [Fact]
    public void Elapsed_time_crosses_as_seconds()
    {
        var source = Source();

        var line = new SourceFreshness(
            source.Id,
            source.Name,
            UpdateCadence.Daily(),
            true,
            new FreshnessAssessment(
                FreshnessState.Overdue,
                FreshnessPolicy.OverdueRule,
                Now.AddHours(-30),
                TimeSpan.FromHours(30)));

        var dto = FreshnessMapper.ToDto(line);

        Assert.Equal(TimeSpan.FromHours(30).TotalSeconds, dto.ElapsedSeconds);
        Assert.True(dto.NeedsRefresh);
    }

    [Fact]
    public void A_null_freshness_line_is_refused() =>
        Assert.Throws<ArgumentNullException>(() => FreshnessMapper.ToDto(null!));

    // ---------- runs and quarantine ----------

    private static IngestionRequest Request() =>
        IngestionRequest.Create(
            SourceId.Create("sec-edgar"),
            DataCategory.CompanyProfile,
            Region.UnitedStates,
            IngestionSubject.Create("Company", "0000320193"),
            CorrelationId.New(),
            Now);

    [Fact]
    public void A_successful_run_reports_what_it_archived()
    {
        var run = IngestionRun.Start(Request(), Now);
        run.RecordArtifact(ContentHash.Compute("payload"u8));
        run.MarkSucceeded(Now);

        var dto = IngestionMapper.ToDto(run);

        Assert.Equal("Succeeded", dto.Outcome);
        Assert.Equal(1, dto.ArtifactCount);
        Assert.Equal("sec-edgar", dto.SourceId);
        Assert.Equal("Company", dto.SubjectKind);
        Assert.Equal("0000320193", dto.SubjectIdentifier);
        Assert.Null(dto.RefusalRuleId);
    }

    [Fact]
    public void A_refused_run_carries_the_rule_that_stopped_it()
    {
        var run = IngestionRun.Refuse(
            Request(),
            "ingestion.source-registered@1",
            "the source is not registered",
            Now);

        var dto = IngestionMapper.ToDto(run);

        // Without this an operator sees that data did not arrive and not why, which turns a
        // compliance decision into an unexplained absence.
        Assert.Equal("Refused", dto.Outcome);
        Assert.Equal("ingestion.source-registered@1", dto.RefusalRuleId);
        Assert.Equal(0, dto.ArtifactCount);
        Assert.NotNull(dto.Reason);
    }

    [Fact]
    public void A_sweep_run_reports_no_subject_identifier()
    {
        var request = IngestionRequest.Create(
            SourceId.Create("sec-edgar"),
            DataCategory.CompanyProfile,
            Region.UnitedStates,
            IngestionSubject.Sweep("Company"),
            CorrelationId.New(),
            Now);

        var dto = IngestionMapper.ToDto(IngestionRun.Start(request, Now));

        Assert.Null(dto.SubjectIdentifier);
        Assert.Null(dto.CompletedAtUtc);
    }

    [Fact]
    public void A_quarantine_record_crosses_without_gaining_anything()
    {
        var payload = QuarantinedPayload.Record(
            ContentHash.Compute(Encoding.UTF8.GetBytes("malformed")),
            SourceId.Create("sec-edgar"),
            DataCategory.CompanyProfile,
            "normalization.unreadable-payload@1",
            "The payload is not readable JSON (JsonException).",
            Now);

        var dto = IngestionMapper.ToDto(payload);

        // The DTO must carry no more than the record does. That record is designed never to hold
        // an excerpt of the payload, and a mapper that helpfully added one would defeat it.
        Assert.Equal(payload.Id.Value, dto.ContentHash);
        Assert.Equal("normalization.unreadable-payload@1", dto.RuleId);
        Assert.Equal(payload.Reason, dto.Reason);
        Assert.Equal(Now, dto.QuarantinedAtUtc);
    }

    [Fact]
    public void Null_records_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => IngestionMapper.ToDto((IngestionRun)null!));
        Assert.Throws<ArgumentNullException>(() => IngestionMapper.ToDto((QuarantinedPayload)null!));
    }
}
