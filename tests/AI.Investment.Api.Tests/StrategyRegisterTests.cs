using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities.Admission;
using AI.Investment.Infrastructure.Admission;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The committed register of declarations, and what it refuses.
/// </summary>
/// <remarks>
/// <para>
/// The register exists because both rehearsals used to build their own declaration and date it to
/// the earliest prediction they were about to score. The look-ahead check was present the whole
/// time and could not fail. These tests pin the three properties that make the file worth reading
/// instead: an entry that changed after it was sealed stops the run, a strategy that is not
/// declared cannot be measured, and a declaration that does not say whether it is a backtest is
/// refused rather than assumed.
/// </para>
/// <para>
/// The last one matters most. If an unstated basis defaulted to prospective, every strategy anybody
/// forgot to mark would claim to have known in advance.
/// </para>
/// </remarks>
public sealed class StrategyRegisterTests
{
    private const string Type = "test-strategy";

    [Fact]
    public void A_declaration_is_rebuilt_from_its_recorded_fields()
    {
        var entry = StrategyRegister.Require(StrategyRegister.Parse(Register()), Type);

        Assert.Equal(Type, entry.Event.Type.Value);
        Assert.Equal(DeclarationBasis.Backtest, entry.Event.Basis);
        Assert.Equal(0.20m, entry.Event.ClaimedBrier);
        Assert.Equal(21, entry.Event.HorizonSessions);
        Assert.Equal("evidence-a", entry.Event.EvidenceBaseFingerprint);
        Assert.Equal(StrategyEvent.NotTuned, entry.Event.TuningBasisFingerprint);
        Assert.Equal(4, entry.TrialsInFamily);
        Assert.Equal("a-family", entry.Event.SearchFamily);
        Assert.Equal(1, entry.FamiliesOnThisEvidenceBase);
        Assert.Equal(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), entry.Event.DeclaredAtUtc);
    }

    /// <summary>
    /// A sealed entry whose fields no longer produce its fingerprint stops the run.
    /// </summary>
    /// <remarks>
    /// This is the only thing standing between a committed declaration and a quietly softened one.
    /// A claim nudged from 0.20 to 0.24 changes nothing visible in a report except the verdict.
    /// </remarks>
    [Fact]
    public void An_entry_edited_after_it_was_sealed_is_refused()
    {
        var tampered = Register(claimedBrier: "0.24", fingerprint: FingerprintOfTheOriginal());

        var error = Assert.Throws<DomainRuleViolationException>(
            () => StrategyRegister.Parse(tampered));

        Assert.Equal("Admission.RegisterTampered", error.Rule);
    }

    /// <summary>And an entry that still matches its fingerprint is accepted.</summary>
    [Fact]
    public void An_untouched_entry_passes_its_own_fingerprint_check()
    {
        var sealedEntry = Register(fingerprint: FingerprintOfTheOriginal());

        var entry = StrategyRegister.Require(StrategyRegister.Parse(sealedEntry), Type);

        Assert.Equal(FingerprintOfTheOriginal(), entry.Event.Fingerprint);
    }

    /// <summary>An entry with no fingerprint yet is accepted, which is how a new one is bootstrapped.</summary>
    [Fact]
    public void An_unsealed_entry_is_accepted_so_it_can_be_sealed()
    {
        var entry = StrategyRegister.Require(StrategyRegister.Parse(Register()), Type);

        Assert.False(string.IsNullOrWhiteSpace(entry.Event.Fingerprint));
    }

    [Fact]
    public void A_strategy_that_is_not_declared_cannot_be_measured()
    {
        var register = StrategyRegister.Parse(Register());

        var error = Assert.Throws<DomainRuleViolationException>(
            () => StrategyRegister.Require(register, "never-declared"));

        Assert.Equal("Admission.StrategyNotDeclared", error.Rule);
    }

    /// <summary>A basis that was left out is refused rather than assumed to be prospective.</summary>
    [Fact]
    public void An_entry_with_no_basis_is_refused() =>
        Assert.ThrowsAny<Exception>(() => StrategyRegister.Parse(Register(basis: null)));

    [Fact]
    public void Two_declarations_of_one_strategy_are_refused()
    {
        var doubled = "[" + Entry() + "," + Entry() + "]";

        Assert.Throws<DomainValidationException>(() => StrategyRegister.Parse(doubled));
    }

    [Fact]
    public void An_empty_register_is_refused() =>
        Assert.Throws<DomainValidationException>(() => StrategyRegister.Parse("[]"));

    [Fact]
    public void A_register_that_is_not_a_list_is_refused() =>
        Assert.Throws<DomainValidationException>(() => StrategyRegister.Parse("{}"));

    [Theory]
    [InlineData("type")]
    [InlineData("direction")]
    [InlineData("evidenceBaseFingerprint")]
    [InlineData("declaredAtUtc")]
    [InlineData("trialsInFamily")]
    [InlineData("searchFamily")]
    public void A_missing_field_is_refused(string field)
    {
        var without = Register(omit: field);

        Assert.ThrowsAny<Exception>(() => StrategyRegister.Parse(without));
    }

    // ---- the two counts the file derives for itself ------------------------------------------

    /// <summary>
    /// Trials in a family are counted from the register, and a bigger declared count wins.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Counting stops a sweep declaring itself trial one; taking the larger of
    /// the two stops the counting from being an upper bound, because variants can be compared
    /// without ever being written down - fifteen attribute columns in one printed table are fifteen
    /// comparisons and a single register entry.
    /// </remarks>
    [Fact]
    public void Trials_in_a_family_are_the_larger_of_declared_and_counted()
    {
        var register = StrategyRegister.Parse(
            "[" +
            Entry(type: "first", family: "a-sweep", trials: "1") + "," +
            Entry(type: "second", family: "a-sweep", trials: "1") + "," +
            Entry(type: "third", family: "a-sweep", trials: "40") +
            "]");

        // Three entries share the family, so a declared one becomes three.
        Assert.Equal(3, StrategyRegister.Require(register, "first").TrialsInFamily);

        // And a declared forty stays forty, because the register cannot see comparisons nobody wrote down.
        Assert.Equal(40, StrategyRegister.Require(register, "third").TrialsInFamily);
    }

    /// <summary>
    /// Families are counted per evidence base, which is what makes opening one cost something.
    /// </summary>
    [Fact]
    public void Families_are_counted_per_evidence_base()
    {
        var register = StrategyRegister.Parse(
            "[" +
            Entry(type: "first", family: "one", evidence: "shared") + "," +
            Entry(type: "second", family: "two", evidence: "shared") + "," +
            Entry(type: "third", family: "three", evidence: "shared") + "," +
            Entry(type: "elsewhere", family: "four", evidence: "separate") +
            "]");

        Assert.Equal(3, StrategyRegister.Require(register, "first").FamiliesOnThisEvidenceBase);
        Assert.Equal(3, StrategyRegister.Require(register, "third").FamiliesOnThisEvidenceBase);
        Assert.Equal(1, StrategyRegister.Require(register, "elsewhere").FamiliesOnThisEvidenceBase);
    }

    /// <summary>
    /// The same hypothesis on a second evidence base is replication, not a further trial.
    /// </summary>
    /// <remarks>
    /// Trials are counted within (family, evidence base). Charging a replication as trial two would
    /// penalise out-of-sample testing, which is the one thing this platform most wants more of.
    /// </remarks>
    [Fact]
    public void Repeating_a_hypothesis_on_new_data_is_not_a_further_trial()
    {
        var register = StrategyRegister.Parse(
            "[" +
            Entry(type: "shallow", family: "one-idea", trials: "1", evidence: "small") + "," +
            Entry(type: "deep", family: "one-idea", trials: "1", evidence: "large") +
            "]");

        Assert.Equal(1, StrategyRegister.Require(register, "shallow").TrialsInFamily);
        Assert.Equal(1, StrategyRegister.Require(register, "deep").TrialsInFamily);
    }

    // ---- fixtures ---------------------------------------------------------------------------

    private static string FingerprintOfTheOriginal() =>
        StrategyRegister.Require(StrategyRegister.Parse(Register()), Type).Event.Fingerprint;

    private static string Register(
        string claimedBrier = "0.20",
        string? basis = "Backtest",
        string? fingerprint = null,
        string? omit = null) =>
        "[" + Entry(claimedBrier, basis, fingerprint, omit) + "]";

    private static string Entry(
        string claimedBrier = "0.20",
        string? basis = "Backtest",
        string? fingerprint = null,
        string? omit = null,
        string type = Type,
        string family = "a-family",
        string trials = "4",
        string evidence = "evidence-a")
    {
        var fields = new List<(string Name, string Value)>
        {
            ("type", "\"" + type + "\""),
            ("direction", "\"Positive\""),
            ("thresholdRatio", "0"),
            ("horizonSessions", "21"),
            ("evidenceBaseFingerprint", "\"" + evidence + "\""),
            ("tuningBasisFingerprint", "\"untuned\""),
            ("declaredAtUtc", "\"2026-09-01T12:00:00Z\""),
            ("claimedBrier", claimedBrier),
            ("trialsInFamily", trials),
            ("searchFamily", "\"" + family + "\""),
            ("note", "\"a fixture\""),
        };

        if (basis is not null)
        {
            fields.Add(("basis", "\"" + basis + "\""));
        }

        if (fingerprint is not null)
        {
            fields.Add(("fingerprint", "\"" + fingerprint + "\""));
        }

        var body = string.Join(
            ",",
            fields
                .Where(f => !string.Equals(f.Name, omit, StringComparison.Ordinal))
                .Select(f => "\"" + f.Name + "\":" + f.Value));

        return "{" + body + "}";
    }
}
