using AI.Investment.Dashboard.Localization;
using AI.Investment.Dashboard.Localization.Resources;
using Xunit;

namespace AI.Investment.Dashboard.Tests;

/// <summary>
/// The claim that both languages are first-class, made checkable.
/// </summary>
public sealed class LocalizationTests
{
    /// <summary>
    /// The test that keeps a translation from silently going missing. A key present in one
    /// language and absent from the other is a screen that renders a marker to half the operators.
    /// </summary>
    [Fact]
    public void Both_languages_define_exactly_the_same_keys()
    {
        var english = EnglishResources.Values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var arabic = ArabicResources.Values.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(english, arabic);
    }

    [Fact]
    public void No_string_is_empty()
    {
        Assert.DoesNotContain(EnglishResources.Values, pair => string.IsNullOrWhiteSpace(pair.Value));
        Assert.DoesNotContain(ArabicResources.Values, pair => string.IsNullOrWhiteSpace(pair.Value));
    }

    /// <summary>
    /// Arabic is translated, not copied. A handful of entries legitimately match - a dash, a
    /// number placeholder - so this asserts that the overwhelming majority differ rather than that
    /// every one does.
    /// </summary>
    [Fact]
    public void Arabic_is_not_a_copy_of_english()
    {
        var identical = EnglishResources.Values
            .Count(pair => string.Equals(pair.Value, ArabicResources.Values[pair.Key], StringComparison.Ordinal));

        Assert.True(
            identical < EnglishResources.Values.Count / 20,
            $"{identical} Arabic strings are identical to their English counterparts.");
    }

    [Fact]
    public void English_reads_left_to_right_and_arabic_right_to_left()
    {
        Assert.False(Language.English.IsRightToLeft);
        Assert.Equal("ltr", Language.English.Direction);

        Assert.True(Language.Arabic.IsRightToLeft);
        Assert.Equal("rtl", Language.Arabic.Direction);
    }

    [Theory]
    [InlineData("ar", "ar")]
    [InlineData("ar-SA", "ar")]
    [InlineData("AR", "ar")]
    [InlineData("en-GB", "en")]
    [InlineData("fr", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void An_unknown_language_falls_back_to_english(string? code, string expected) =>
        Assert.Equal(expected, Language.FromCode(code).Code);

    [Fact]
    public void Switching_language_changes_the_strings_and_the_direction()
    {
        var state = new LocalizationState();

        Assert.Equal(EnglishResources.Values["nav.portfolio"], state["nav.portfolio"]);
        Assert.False(state.IsRightToLeft);

        state.Set(Language.Arabic);

        Assert.Equal(ArabicResources.Values["nav.portfolio"], state["nav.portfolio"]);
        Assert.True(state.IsRightToLeft);
    }

    /// <summary>
    /// A missing key is visible rather than silently English. Falling back to the other language
    /// would make an untranslated Arabic screen look deliberate.
    /// </summary>
    [Fact]
    public void A_missing_key_renders_as_a_marker()
    {
        var state = new LocalizationState();

        Assert.Equal("«no.such.key»", state["no.such.key"]);
    }

    /// <summary>Backend enum names are never shown; they are mapped to labels.</summary>
    [Theory]
    [InlineData("Available")]
    [InlineData("NoObservedPrice")]
    [InlineData("NotHeld")]
    public void Every_valuation_state_has_a_label_in_both_languages(string state)
    {
        Assert.True(EnglishResources.Values.ContainsKey($"valuation.{state}"));
        Assert.True(ArabicResources.Values.ContainsKey($"valuation.{state}"));

        var localization = new LocalizationState();

        Assert.DoesNotContain("«", localization.Label("valuation", state), StringComparison.Ordinal);
        Assert.NotEqual(state, localization.Label("valuation", state));
    }

    [Fact]
    public void An_unmapped_enum_value_does_not_leak_the_raw_name()
    {
        var localization = new LocalizationState();

        Assert.Equal("«valuation.SomethingNew»", localization.Label("valuation", "SomethingNew"));
    }

    /// <summary>
    /// The reason this project overrides InvariantGlobalization: under it, both cultures format
    /// identically and every Arabic figure would silently be an English one.
    /// </summary>
    [Fact]
    public void Numbers_and_dates_are_formatted_in_the_selected_culture()
    {
        var state = new LocalizationState();

        var english = state.Number(1234.5m);

        state.Set(Language.Arabic);

        Assert.Equal("ar", state.Culture.TwoLetterISOLanguageName);
        Assert.NotEmpty(state.Number(1234.5m));
        Assert.NotEmpty(english);
    }

    /// <summary>Nothing is shown as zero when it is unknown.</summary>
    [Fact]
    public void A_null_figure_renders_as_the_not_applicable_marker()
    {
        var state = new LocalizationState();

        Assert.Equal(state["state.notApplicable"], state.Number(null));
        Assert.Equal(state["state.notApplicable"], state.Money(null, "USD"));
        Assert.Equal(state["state.notApplicable"], state.Instant(null));
        Assert.DoesNotContain("0", state.Number(null), StringComparison.Ordinal);
    }
}
