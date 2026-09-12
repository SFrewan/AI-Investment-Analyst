using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Evidence;

/// <summary>
/// Tier 4 of the evidence spine: a derived value recorded so it can be reproduced.
/// </summary>
/// <remarks>
/// The properties that matter are all about refusal - refusing a kind whose rules nothing enforces,
/// refusing a confidence on something exact, refusing inputs that are not named, and refusing to
/// re-order inputs whose order the rule depended on. A determination that accepted any of those
/// would still look like evidence and would no longer be reproducible.
/// </remarks>
public sealed class DeterminationTests
{
    private static readonly DateTime AsOf = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private static readonly ContentHash First = ContentHash.Compute("first"u8);
    private static readonly ContentHash Second = ContentHash.Compute("second"u8);
    private static readonly ContentHash Third = ContentHash.Compute("third"u8);

    // ---------------------------------------------------------------- DeterminationId

    [Fact]
    public void A_determination_id_refuses_the_empty_guid()
    {
        Assert.Throws<ArgumentException>(() => DeterminationId.Create(Guid.Empty));
    }

    [Fact]
    public void A_determination_id_has_value_equality()
    {
        var value = Guid.NewGuid();

        Assert.Equal(DeterminationId.Create(value), DeterminationId.Create(value));
        Assert.NotEqual(DeterminationId.New(), DeterminationId.New());
        Assert.Equal(value, DeterminationId.Create(value).Value);
    }

    // ---------------------------------------------------------------- construction

    [Fact]
    public void A_calculation_is_recorded_with_everything_a_recomputation_needs()
    {
        var determination = Record();

        Assert.Equal(ClaimKind.Calculation, determination.Kind);
        Assert.Equal("gate6-coverage", determination.RuleId);
        Assert.Equal("1", determination.RuleVersion);
        Assert.Equal([First, Second], determination.Inputs);
        Assert.Equal("24/130", determination.OutputCanonical);
        Assert.Equal(AsOf, determination.AsOfUtc);
        Assert.Equal(Now, determination.RecordedAtUtc);
        Assert.Null(determination.Confidence);

        // The output hash is of the canonical output, so "byte-equal" is a comparison.
        Assert.Equal(ContentHash.Compute("24/130"u8), determination.OutputContentHash);
    }

    [Theory]
    [InlineData(ClaimKind.Fact)]
    [InlineData(ClaimKind.AiInterpretation)]
    [InlineData(ClaimKind.Prediction)]
    public void Only_a_calculation_may_be_recorded_today(ClaimKind kind)
    {
        var thrown = Assert.Throws<DomainRuleViolationException>(() => Record(kind: kind));

        Assert.Contains("Refusing", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_calculation_may_not_carry_its_own_confidence()
    {
        var thrown = Assert.Throws<DomainRuleViolationException>(
            () => Record(confidence: Confidence.Create(0.9m)));

        Assert.Contains("exact given its inputs", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_determination_id_is_required()
    {
        // Called directly rather than through the helper, whose defaulting would replace the
        // default id and quietly test nothing.
        Assert.Throws<DomainValidationException>(() => Determination.Record(
            default,
            ClaimKind.Calculation,
            "gate6-coverage",
            "1",
            [First, Second],
            "24/130",
            AsOf,
            Now));
    }

    [Theory]
    [InlineData("", "1")]
    [InlineData("   ", "1")]
    [InlineData("gate6-coverage", "")]
    [InlineData("gate6-coverage", "   ")]
    public void The_rule_and_its_version_are_both_required(string ruleId, string ruleVersion)
    {
        Assert.Throws<DomainValidationException>(() => Record(ruleId: ruleId, ruleVersion: ruleVersion));
    }

    [Fact]
    public void A_determination_must_name_the_inputs_it_was_computed_from()
    {
        var thrown = Assert.Throws<DomainRuleViolationException>(() => Record(inputs: []));

        Assert.Contains("cannot be reproduced", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inputs_may_not_be_null_and_may_not_contain_a_null()
    {
        // The null case goes direct, for the same reason the id case does.
        Assert.Throws<ArgumentNullException>(() => Determination.Record(
            DeterminationId.New(), ClaimKind.Calculation, "gate6-coverage", "1",
            null!, "24/130", AsOf, Now));

        Assert.Throws<DomainValidationException>(() => Record(inputs: [First, null!]));
    }

    [Fact]
    public void An_output_is_required_because_there_must_be_something_to_reproduce()
    {
        Assert.Throws<DomainValidationException>(() => Record(output: "   "));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Both_instants_must_be_utc_and_are_not_silently_converted(DateTimeKind kind)
    {
        var wrong = new DateTime(2026, 8, 31, 0, 0, 0, kind);

        Assert.ThrowsAny<Exception>(() => Record(asOfUtc: wrong));
        Assert.ThrowsAny<Exception>(() => Record(recordedAtUtc: wrong));
    }

    // ---------------------------------------------------------------- immutability

    [Fact]
    public void A_determination_exposes_no_way_to_change_what_it_concluded()
    {
        var settable = typeof(Determination).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(settable);

        Assert.DoesNotContain(
            typeof(Determination).GetMethods().Where(m => m.DeclaringType == typeof(Determination)),
            m => m.Name.StartsWith("Set", StringComparison.Ordinal)
                || m.Name.StartsWith("Change", StringComparison.Ordinal)
                || m.Name.StartsWith("Update", StringComparison.Ordinal)
                || m.Name.StartsWith("Correct", StringComparison.Ordinal));
    }

    [Fact]
    public void The_inputs_collection_cannot_be_mutated_through_the_exposed_list()
    {
        var determination = Record();

        // The exposed collection must not cast back to a mutable list. Returning the backing list
        // itself types as read-only and casts straight back, which is the hole this closes.
        Assert.Throws<NotSupportedException>(
            () => ((IList<ContentHash>)determination.Inputs).Add(Third));

        Assert.Equal(2, determination.Inputs.Count);
    }

    // ---------------------------------------------------------------- input ordering

    [Fact]
    public void Input_order_is_preserved_exactly_and_never_sorted()
    {
        var forwards = Record(inputs: [First, Second, Third]);
        var backwards = Record(inputs: [Third, Second, First]);

        Assert.Equal([First, Second, Third], forwards.Inputs);
        Assert.Equal([Third, Second, First], backwards.Inputs);
    }

    [Fact]
    public void The_inputs_digest_is_order_sensitive()
    {
        // If this ever stopped being true, two different computations would share a reproducibility
        // key and the unique index would refuse the second one as a duplicate of something else.
        Assert.NotEqual(
            Determination.ComputeInputsHash([First, Second]),
            Determination.ComputeInputsHash([Second, First]));
    }

    [Fact]
    public void The_inputs_digest_is_deterministic_across_calls()
    {
        Assert.Equal(
            Determination.ComputeInputsHash([First, Second, Third]),
            Determination.ComputeInputsHash([First, Second, Third]));
    }

    [Fact]
    public void The_stored_digest_matches_the_inputs_beside_it()
    {
        var determination = Record(inputs: [First, Second, Third]);

        Assert.True(determination.InputsHashMatchesInputs());
        Assert.Equal(
            Determination.ComputeInputsHash([First, Second, Third]).Value,
            determination.InputsHash);
    }

    // ---------------------------------------------------------------- reproducibility key

    [Fact]
    public void Two_determinations_of_the_same_rule_version_instant_and_inputs_share_a_key()
    {
        var one = Record();
        var two = Record();

        Assert.NotEqual(one.Id, two.Id);
        Assert.True(one.HasSameReproducibilityKeyAs(two));
    }

    [Fact]
    public void Changing_any_part_of_the_key_makes_it_a_different_determination()
    {
        var baseline = Record();

        Assert.False(baseline.HasSameReproducibilityKeyAs(Record(ruleId: "other-rule")));
        Assert.False(baseline.HasSameReproducibilityKeyAs(Record(ruleVersion: "2")));
        Assert.False(baseline.HasSameReproducibilityKeyAs(Record(asOfUtc: AsOf.AddDays(-1))));
        Assert.False(baseline.HasSameReproducibilityKeyAs(Record(inputs: [Second, First])));
    }

    [Fact]
    public void The_output_is_not_part_of_the_reproducibility_key()
    {
        // Two rows with the same key and different outputs mean one of them is wrong, which is
        // exactly what the unique index must refuse rather than store.
        var one = Record(output: "24/130");
        var two = Record(output: "19/125");

        Assert.True(one.HasSameReproducibilityKeyAs(two));
        Assert.NotEqual(one.OutputContentHash, two.OutputContentHash);
    }

    /// <summary>
    /// A determination has no reference to what it was about, and that is the design.
    /// </summary>
    [Fact]
    public void A_determination_references_no_subject_row_of_any_kind()
    {
        var names = typeof(Determination).GetProperties().Select(p => p.Name).ToList();

        foreach (var forbidden in new[]
        {
            "SecurityId", "UniverseId", "CompanyId", "ObservationId", "Cik", "Ticker", "Subject",
        })
        {
            Assert.DoesNotContain(forbidden, names);
        }
    }

    private static Determination Record(
        DeterminationId? id = null,
        ClaimKind kind = ClaimKind.Calculation,
        string ruleId = "gate6-coverage",
        string ruleVersion = "1",
        IEnumerable<ContentHash>? inputs = null,
        string output = "24/130",
        DateTime? asOfUtc = null,
        DateTime? recordedAtUtc = null,
        Confidence? confidence = null) =>
        Determination.Record(
            id ?? DeterminationId.New(),
            kind,
            ruleId,
            ruleVersion,
            inputs ?? [First, Second],
            output,
            asOfUtc ?? AsOf,
            recordedAtUtc ?? Now,
            confidence);
}
