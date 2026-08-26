using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Observations;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Observations;

/// <summary>
/// The canonical form, and the refusals that keep it honest.
/// </summary>
/// <remarks>
/// Two properties carry the weight. A value round-trips through its canonical string without
/// changing, and reading a value as the wrong type throws rather than producing a plausible number.
/// The second matters more: a fabricated figure with real provenance is indistinguishable from a
/// true one, and this is the only place able to refuse it.
/// </remarks>
public sealed class ObservationValueTests
{
    [Fact]
    public void Text_is_trimmed_and_kept()
    {
        var value = ObservationValue.Text("  Apple Inc.  ");

        Assert.Equal(ObservationValueKind.Text, value.Kind);
        Assert.Equal("Apple Inc.", value.Canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_is_refused(string blank) =>

        // A blank value is an absence, and an absence recorded as an observation is worse than a
        // gap: a gap is visible in a query, a blank string is not.
        Assert.Throws<DomainValidationException>(() => ObservationValue.Text(blank));

    [Fact]
    public void An_overlong_text_value_is_refused()
    {
        var tooLong = new string('x', ObservationValue.MaxTextLength + 1);

        Assert.Throws<DomainValidationException>(() => ObservationValue.Text(tooLong));
    }

    [Fact]
    public void A_number_is_canonicalised_culture_invariantly()
    {
        var value = ObservationValue.Number(1234.5m);

        // Never "1234,5". A value written under one locale and read under another is the defect
        // that surfaces months later as a figure a thousand times too large.
        Assert.Equal("1234.5", value.Canonical);
        Assert.DoesNotContain(",", value.Canonical, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0001")]
    [InlineData("123456789012345678901234567")]
    public void A_number_round_trips(string literal)
    {
        var original = decimal.Parse(literal, CultureInfo.InvariantCulture);

        Assert.Equal(original, ObservationValue.Number(original).AsNumber());
    }

    [Fact]
    public void A_boolean_round_trips()
    {
        Assert.True(ObservationValue.Boolean(true).AsBoolean());
        Assert.False(ObservationValue.Boolean(false).AsBoolean());
    }

    [Fact]
    public void A_timestamp_round_trips_as_UTC()
    {
        var original = new DateTime(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);

        var restored = ObservationValue.Timestamp(original).AsTimestamp();

        Assert.Equal(original, restored);
        Assert.Equal(DateTimeKind.Utc, restored.Kind);
    }

    [Fact]
    public void A_local_timestamp_is_refused()
    {
        var local = new DateTime(2026, 3, 14, 9, 26, 53, DateTimeKind.Local);

        Assert.Throws<DomainValidationException>(() => ObservationValue.Timestamp(local));
    }

    [Fact]
    public void A_timestamp_of_unspecified_kind_is_refused()
    {
        // Unspecified is the dangerous one. It looks like a valid timestamp and silently means
        // "whatever zone the reader happens to be in".
        var unspecified = new DateTime(2026, 3, 14, 9, 26, 53, DateTimeKind.Unspecified);

        Assert.Throws<DomainValidationException>(() => ObservationValue.Timestamp(unspecified));
    }

    [Fact]
    public void Reading_text_as_a_number_throws_rather_than_parsing_it() =>
        Assert.Throws<DomainRuleViolationException>(() => ObservationValue.Text("3571").AsNumber());

    [Fact]
    public void Reading_text_as_a_boolean_throws() =>
        Assert.Throws<DomainRuleViolationException>(() => ObservationValue.Text("true").AsBoolean());

    [Fact]
    public void Reading_text_as_a_timestamp_throws() =>
        Assert.Throws<DomainRuleViolationException>(
            () => ObservationValue.Text("2026-03-14T09:26:53Z").AsTimestamp());

    [Fact]
    public void Every_kind_can_be_restored_from_its_stored_form()
    {
        var timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        Assert.Equal("Apple", ObservationValue.Restore(ObservationValueKind.Text, "Apple").Canonical);
        Assert.Equal(42m, ObservationValue.Restore(ObservationValueKind.Number, "42").AsNumber());
        Assert.True(ObservationValue.Restore(ObservationValueKind.Boolean, "true").AsBoolean());
        Assert.Equal(
            timestamp,
            ObservationValue
                .Restore(
                    ObservationValueKind.Timestamp,
                    timestamp.ToString("O", CultureInfo.InvariantCulture))
                .AsTimestamp());
    }

    [Fact]
    public void An_unknown_kind_is_refused_rather_than_read_as_text() =>

        // Unknown is the default enum value, which is what a corrupted or future-written row
        // deserialises to. Refusing is the only safe reading: a value of uncertain type is not a
        // value, and defaulting it to text would let a number silently stop being one.
        Assert.Throws<DomainValidationException>(
            () => ObservationValue.Restore(ObservationValueKind.Unknown, "something"));

    [Fact]
    public void A_kind_this_build_does_not_recognise_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => ObservationValue.Restore((ObservationValueKind)9999, "something"));

    [Fact]
    public void A_stored_number_that_will_not_parse_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => ObservationValue.Restore(ObservationValueKind.Number, "not-a-number"));

    [Fact]
    public void A_blank_stored_value_is_refused() =>
        Assert.Throws<DomainValidationException>(
            () => ObservationValue.Restore(ObservationValueKind.Text, "   "));
}
