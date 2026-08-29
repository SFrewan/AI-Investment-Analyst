using System.Globalization;

namespace AI.Investment.Dashboard.Localization;

/// <summary>A language this product ships in, with the direction its script reads.</summary>
/// <remarks>
/// Both are first-class. Arabic is not English with the strings replaced: it changes the document
/// direction, the layout mirroring and the formatting of every date and number on the screen.
/// </remarks>
public sealed record Language
{
    private Language(string code, string nativeName, string direction)
    {
        Code = code;
        NativeName = nativeName;
        Direction = direction;
    }

    /// <summary>English, left to right.</summary>
    public static Language English { get; } = new("en", "English", "ltr");

    /// <summary>Arabic, right to left.</summary>
    public static Language Arabic { get; } = new("ar", "العربية", "rtl");

    /// <summary>Every supported language, in the order the switcher shows them.</summary>
    public static IReadOnlyList<Language> All { get; } = [English, Arabic];

    /// <summary>The BCP-47 code, and the key the preference is stored under.</summary>
    public string Code { get; }

    /// <summary>The language's name in its own script. Never translated.</summary>
    public string NativeName { get; }

    /// <summary>The value of the document's <c>dir</c> attribute.</summary>
    public string Direction { get; }

    public bool IsRightToLeft => string.Equals(Direction, "rtl", StringComparison.Ordinal);

    /// <summary>The culture used to format dates and numbers in this language.</summary>
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Code);

    /// <summary>
    /// The language for a code, falling back to English rather than throwing.
    /// </summary>
    /// <remarks>
    /// A stored preference can outlive the language it names, and a browser can offer a code
    /// nobody planned for. Neither is a reason to show an error page.
    /// </remarks>
    public static Language FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return English;
        }

        var trimmed = code.Trim();

        foreach (var language in All)
        {
            if (trimmed.StartsWith(language.Code, StringComparison.OrdinalIgnoreCase))
            {
                return language;
            }
        }

        return English;
    }
}
