using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Admission;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Opportunities;

/// <summary>
/// The two decisions: what the trial budget counts within, and how a claim may be stated.
/// </summary>
/// <remarks>
/// <para>
/// Both were changes to the admission criteria themselves, which is the most dangerous kind of
/// change this codebase can make: a criterion that quietly got easier looks identical to one that
/// got better, and the difference only shows up as a strategy that should not have been admitted.
/// So the first tests here are the ones that pin what did <em>not</em> change, and the rest pin
/// that each new degree of freedom is paid for.
/// </para>
/// <para>
/// The two payments are worth naming. Opening a fresh search family escapes the trial budget - and
/// raises the significance correction that every strategy on that evidence base is sized at,
/// including the one that opened it. Claiming a stronger score buys a smaller sample - and becomes
/// a score the strategy must actually deliver, by <see cref="AdmissionRefusal.FellShortOfItsOwnClaim"/>.
/// Neither is free, and neither can be taken back after the fact, because both live inside the
/// declaration's fingerprint.
/// </para>
/// </remarks>
public sealed class SearchFamilyAndClaimTests
{
    private static readonly DateTime Declared = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstPrediction = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Measured = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);

    private const string Evidence = "evidence-a";
    private const string OtherEvidence = "evidence-b";

    private static readonly OpportunityType Type = OpportunityType.Create("a-strategy");

    private static readonly AdmissionCriteria Criteria = AdmissionCriteria.Standard;

    // ---- what did not change ---------------------------------------------------------------

    /// <summary>
    /// One family on one evidence base behaves exactly as the old per-evidence-base rule did.
    /// </summary>
    /// <remarks>
    /// The compatibility assertion. Every declaration written before search families existed is,
    /// in the new scheme, a single family with a single member on its evidence base - so its budget
    /// position and its significance are the ones it always had. If this ever fails, the change
    /// stopped being a refinement and became a different bar.
    /// </remarks>
    [Fact]
    public void A_lone_family_on_a_lone_evidence_base_is_scored_exactly_as_before()
    {
        var requirement = SampleRequirement.For(
            Declaration(),
            Scored(families: 1),
            Criteria);

        Assert.Equal(Criteria.Significance, requirement.EffectiveSignificance);
        Assert.False(requirement.ClaimWasRelative);

        // 0.20 claimed against a 0.25 reference with a 0.30 spread: the published figure.
        Assert.InRange(requirement.Derived, 200, 250);
    }

    /// <summary>A strategy claiming exactly the ceiling is judged by the ceiling, as it always was.</summary>
    [Fact]
    public void Claiming_the_ceiling_adds_no_new_refusal()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(claimedBrier: Criteria.MaximumBrierScore),
            Scored(brier: 0.19m, families: 1),
            Criteria,
            Now);

        Assert.DoesNotContain(AdmissionRefusal.FellShortOfItsOwnClaim, admission.Refusals);
    }

    // ---- the trial budget counts within a family ---------------------------------------------

    /// <summary>Variants of one search are still caught, which is the whole point of the budget.</summary>
    [Fact]
    public void Many_variants_in_one_family_are_still_refused()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(searchFamily: "a-sweep"),
            Scored(trials: 16, families: 1),
            Criteria,
            Now);

        Assert.Contains(AdmissionRefusal.TooManyTrialsAgainstOneEvidenceBase, admission.Refusals);
    }

    /// <summary>An independent hypothesis in its own family is not accused of being one of them.</summary>
    [Fact]
    public void A_hypothesis_in_its_own_family_is_not_charged_for_another_search()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(searchFamily: "its-own"),
            Scored(trials: 1, families: 6),
            Criteria,
            Now);

        Assert.DoesNotContain(AdmissionRefusal.TooManyTrialsAgainstOneEvidenceBase, admission.Refusals);
    }

    /// <summary>
    /// But opening families is not free: each one tightens the significance every sample is sized at.
    /// </summary>
    /// <remarks>
    /// This is the test that stops the family being a loophole. If it ever fails, a strategy can
    /// escape the trial budget by naming a fresh family and pay nothing for it.
    /// </remarks>
    [Fact]
    public void Every_extra_family_raises_the_sample_every_strategy_needs()
    {
        var alone = SampleRequirement.For(Declaration(), Scored(families: 1), Criteria);
        var crowded = SampleRequirement.For(Declaration(), Scored(families: 5), Criteria);

        Assert.True(crowded.EffectiveSignificance < alone.EffectiveSignificance);
        Assert.Equal(Criteria.Significance / 5m, crowded.EffectiveSignificance);
        Assert.True(crowded.Derived > alone.Derived);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(20)]
    public void The_requirement_never_falls_as_families_are_added(int families)
    {
        var requirement = SampleRequirement.For(Declaration(), Scored(families: families), Criteria);
        var lonely = SampleRequirement.For(Declaration(), Scored(families: 1), Criteria);

        Assert.True(requirement.Derived >= lonely.Derived);
        Assert.True(requirement.Required >= Criteria.MinimumResolvedPredictions);
    }

    [Fact]
    public void A_measurement_belonging_to_no_family_is_refused() =>
        Assert.Throws<DomainValidationException>(() => Scored(families: 0));

    /// <summary>The family is inside the fingerprint, so it cannot be renamed after the fact.</summary>
    [Fact]
    public void Renaming_the_family_changes_the_declaration()
    {
        Assert.NotEqual(
            Declaration(searchFamily: "a-sweep").Fingerprint,
            Declaration(searchFamily: "its-own").Fingerprint);
    }

    /// <summary>A declaration that names no family opens one of its own, named for itself.</summary>
    [Fact]
    public void An_unnamed_family_is_the_strategy_itself() =>
        Assert.Equal(Type.Value, Declaration().SearchFamily);

    // ---- claims may be relative ----------------------------------------------------------------

    /// <summary>
    /// A relative claim resolves against whatever base rate the strategy actually faces.
    /// </summary>
    /// <remarks>
    /// The failure this exists for: an event happening 87 per cent of the time has a base-rate
    /// score of 0.1112, so an absolute claim of 0.20 promises to do worse than nothing. A claim of
    /// "a quarter of the base rate removed" is well formed at any base rate, and cannot be
    /// accidentally incoherent.
    /// </remarks>
    [Fact]
    public void A_relative_claim_is_coherent_where_an_absolute_one_is_not()
    {
        var absolute = SampleRequirement.For(
            Declaration(claimedBrier: 0.20m),
            Scored(baseRate: 0.8725m, families: 1),
            Criteria);

        var relative = SampleRequirement.For(
            Declaration(claimedSkill: 0.25m),
            Scored(baseRate: 0.8725m, families: 1),
            Criteria);

        Assert.True(absolute.IsUnattainable);
        Assert.False(relative.IsUnattainable);
        Assert.True(relative.ClaimWasRelative);

        // A quarter removed from a 0.1112 reference.
        Assert.Equal(0.75m * relative.ReferenceBrier!.Value, relative.ClaimedBrier);
    }

    /// <summary>A relative claim can never be unattainable, whatever the base rate.</summary>
    [Theory]
    [InlineData(0.02)]
    [InlineData(0.30)]
    [InlineData(0.50)]
    [InlineData(0.97)]
    public void A_relative_claim_is_attainable_at_any_base_rate(double baseRate)
    {
        var requirement = SampleRequirement.For(
            Declaration(claimedSkill: 0.20m),
            Scored(baseRate: (decimal)baseRate, families: 1),
            Criteria);

        Assert.False(requirement.IsUnattainable);
        Assert.True(requirement.ClaimedBrier < requirement.ReferenceBrier);
    }

    /// <summary>
    /// And a claim that buys a smaller sample has to be delivered.
    /// </summary>
    /// <remarks>
    /// Without this, claiming strongly is free: promise a very good score, collect the smaller
    /// sample requirement, then hand in a mediocre score and pass on the shipped ceiling instead.
    /// </remarks>
    [Fact]
    public void A_strategy_that_misses_its_own_stronger_claim_is_refused()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(claimedBrier: 0.05m),
            Scored(brier: 0.15m, families: 1),
            Criteria,
            Now);

        Assert.Contains(AdmissionRefusal.FellShortOfItsOwnClaim, admission.Refusals);
    }

    [Fact]
    public void A_strategy_that_meets_its_own_stronger_claim_is_not_refused_for_it()
    {
        var admission = StrategyAdmission.Evaluate(
            Declaration(claimedBrier: 0.05m),
            Scored(brier: 0.04m, families: 1),
            Criteria,
            Now);

        Assert.DoesNotContain(AdmissionRefusal.FellShortOfItsOwnClaim, admission.Refusals);
    }

    [Fact]
    public void Two_claims_at_once_are_refused() =>
        Assert.Throws<DomainValidationException>(() => StrategyEvent.Declare(
            Type,
            PredictionDirection.Positive,
            Percentage.FromRatio(0m),
            horizonSessions: 21,
            Evidence,
            Declared,
            tuningBasisFingerprint: OtherEvidence,
            claimedBrier: 0.20m,
            claimedSkill: 0.25m));

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void A_claimed_share_outside_the_scale_is_refused(double share) =>
        Assert.Throws<DomainValidationException>(() => Declaration(claimedSkill: (decimal)share));

    /// <summary>The claimed share is inside the fingerprint, like every other part of the claim.</summary>
    [Fact]
    public void Softening_a_relative_claim_changes_the_declaration() =>
        Assert.NotEqual(
            Declaration(claimedSkill: 0.25m).Fingerprint,
            Declaration(claimedSkill: 0.10m).Fingerprint);

    // ---- fixtures ---------------------------------------------------------------------------

    private static StrategyEvent Declaration(
        decimal? claimedBrier = 0.20m,
        decimal? claimedSkill = null,
        string? searchFamily = null) =>
        StrategyEvent.Declare(
            Type,
            PredictionDirection.Positive,
            Percentage.FromRatio(0m),
            horizonSessions: 21,
            Evidence,
            Declared,
            tuningBasisFingerprint: OtherEvidence,
            claimedBrier: claimedSkill is null ? claimedBrier : null,
            basis: DeclarationBasis.Prospective,
            claimedSkill: claimedSkill,
            searchFamily: searchFamily);

    private static StrategyMeasurement Scored(
        decimal brier = 0.10m,
        int trials = 1,
        int families = 1,
        decimal baseRate = 0.5m) =>
        StrategyMeasurement.Create(
            Type,
            400,
            Measurement.Measured(brier, 400, "measured"),
            Percentage.FromRatio(0m),
            FirstPrediction,
            Measured,
            Evidence,
            trialsInFamily: trials,
            familiesOnThisEvidenceBase: families,
            baseRate: baseRate,
            componentStandardDeviation: 0.30m);
}
