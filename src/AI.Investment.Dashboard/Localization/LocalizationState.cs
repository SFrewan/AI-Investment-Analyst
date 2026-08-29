using System.Globalization;
using AI.Investment.Dashboard.Localization.Resources;

namespace AI.Investment.Dashboard.Localization;

/// <summary>
/// The current language, the strings for it, and the formatting that follows from it.
/// </summary>
/// <remarks>
/// <para>
/// One service rather than a static helper, because switching language has to re-render the
/// application and change the document's direction. Components subscribe to
/// <see cref="Changed"/>; nothing reads a culture from a thread.
/// </para>
/// <para>
/// <strong>A missing key is visible, not silent.</strong> It renders inside guillemets rather than
/// falling back to English, so an untranslated string is obvious in Arabic instead of looking like
/// a deliberate choice. A test asserts the two resource sets have identical keys, so this should
/// never be reached.
/// </para>
/// </remarks>
public sealed class LocalizationState
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _resources = new(
        StringComparer.Ordinal)
    {
        [Language.English.Code] = EnglishResources.Values,
        [Language.Arabic.Code] = ArabicResources.Values,
    };

    /// <summary>Raised after the language changes, so the shell can re-render.</summary>
    public event Action? Changed;

    public Language Current { get; private set; } = Language.English;

    /// <summary>The culture every date and number on screen is formatted with.</summary>
    public CultureInfo Culture => Current.Culture;

    public bool IsRightToLeft => Current.IsRightToLeft;

    /// <summary>The translated string for a key.</summary>
    public string this[string key] =>
        _resources[Current.Code].TryGetValue(key, out var value) ? value : $"«{key}»";

    /// <summary>A translated string with positional arguments, formatted in the current culture.</summary>
    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, this[key], arguments);

    /// <summary>
    /// The display label for a backend enum value, by convention <c>{group}.{value}</c>.
    /// </summary>
    /// <remarks>
    /// Backend enum names are identifiers, not UI. An unmapped value renders as the missing-key
    /// marker rather than as the raw name, so a new backend state is caught rather than leaking
    /// an English identifier into an Arabic screen.
    /// </remarks>
    public string Label(string group, string? value) =>
        this[$"{group}.{(string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim())}"];

    public void Set(Language language)
    {
        ArgumentNullException.ThrowIfNull(language);

        if (string.Equals(language.Code, Current.Code, StringComparison.Ordinal))
        {
            return;
        }

        Current = language;

        // Set so that any framework component formatting on its own still agrees with this one.
        CultureInfo.DefaultThreadCurrentCulture = language.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = language.Culture;

        Changed?.Invoke();
    }

    /// <summary>Money, in the current culture, with the currency stated rather than symbolised.</summary>
    /// <remarks>
    /// The code rather than a symbol: this platform is single-currency today and will not be
    /// forever, and a bare symbol is the shortest path to reading one currency as another.
    /// </remarks>
    public string Money(decimal? amount, string? currency) =>
        amount is null
            ? this["state.notApplicable"]
            : string.Create(Culture, $"{amount.Value:N2} {currency}").Trim();

    /// <summary>A number, or the not-applicable marker when there is none.</summary>
    public string Number(decimal? value, int decimals = 2) =>
        value is null
            ? this["state.notApplicable"]
            : value.Value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), Culture);

    /// <summary>An instant, in the current culture, always stated as UTC.</summary>
    /// <remarks>
    /// Every timestamp this platform stores is UTC, and rendering one in a browser's local zone
    /// would silently move a market date across a day boundary.
    /// </remarks>
    public string Instant(DateTime? value) =>
        value is null
            ? this["state.notApplicable"]
            : value.Value.ToString("yyyy-MM-dd HH:mm", Culture) + " UTC";

    /// <summary>A calendar date without a time.</summary>
    public string Date(DateTime? value) =>
        value is null ? this["state.notApplicable"] : value.Value.ToString("yyyy-MM-dd", Culture);
}
