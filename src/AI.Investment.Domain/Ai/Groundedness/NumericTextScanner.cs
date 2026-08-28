using System.Globalization;

namespace AI.Investment.Domain.Ai.Groundedness;

/// <summary>
/// Finds every number written in a piece of agent prose.
/// </summary>
/// <remarks>
/// <para>
/// The backstop behind the structured figure list. An agent that states its numbers only in named,
/// cited fields is easy to check; the risk is the one that writes a figure into a summary sentence
/// instead, where a structural check never looks. This scanner is what makes that route as visible
/// as the other.
/// </para>
/// <para>
/// Hand-written rather than a regular expression, for two reasons. The scale and percentage
/// suffixes need to be turned into alternative <em>values</em> rather than merely matched, which a
/// pattern cannot do on its own; and a compiled pattern here would sit behind an analyzer
/// suggestion to move to source generation, which is a lot of machinery for a scan over a few
/// sentences.
/// </para>
/// <para>
/// It is deliberately eager: <c>3</c> in "3 risks" is reported as a mention. Prompts therefore
/// instruct agents to write small counts as words. A false positive costs a refusal, which is
/// recoverable; a false negative is a fabricated figure reaching a score, which is not.
/// </para>
/// </remarks>
public static class NumericTextScanner
{
    private static readonly (string Suffix, decimal Multiplier)[] ScaleSuffixes =
    [
        ("trillion", 1_000_000_000_000m),
        ("billion", 1_000_000_000m),
        ("million", 1_000_000m),
        ("thousand", 1_000m),
        ("bn", 1_000_000_000m),
        ("tn", 1_000_000_000_000m),
        ("mm", 1_000_000m),
        ("m", 1_000_000m),
        ("k", 1_000m),
        ("b", 1_000_000_000m),
    ];

    /// <summary>Every numeric literal in <paramref name="text"/>, in the order they appear.</summary>
    public static List<NumericMention> Scan(string? text)
    {
        var mentions = new List<NumericMention>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return mentions;
        }

        var index = 0;

        while (index < text.Length)
        {
            if (!char.IsAsciiDigit(text[index]))
            {
                index++;
                continue;
            }

            var start = index;

            // A leading '-' is a sign only when it does not sit between two digits. Without this,
            // the '-02' inside a date such as 2026-02-10 is read as negative two, and every agent
            // that mentions a date is reported for quoting a number nobody wrote.
            if (start > 0 &&
                (text[start - 1] == '-' || text[start - 1] == '+') &&
                (start < 2 || !char.IsAsciiDigit(text[start - 2])))
            {
                start--;
            }

            var end = index;

            while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == ','))
            {
                end++;
            }

            if (end < text.Length - 1 && text[end] == '.' && char.IsAsciiDigit(text[end + 1]))
            {
                end++;

                while (end < text.Length && char.IsAsciiDigit(text[end]))
                {
                    end++;
                }
            }

            var literal = text[start..end];

            if (decimal.TryParse(
                    literal.Replace(",", string.Empty, StringComparison.Ordinal),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                var suffixEnd = ReadSuffix(text, end, value, out var candidates);

                mentions.Add(new NumericMention(text[start..suffixEnd], candidates));

                index = suffixEnd;
                continue;
            }

            index = end;
        }

        return mentions;
    }

    /// <summary>
    /// Consumes a percentage or scale suffix following a literal and produces the values it may
    /// denote.
    /// </summary>
    /// <returns>The index just past the suffix, or <paramref name="end"/> when there was none.</returns>
    private static int ReadSuffix(string text, int end, decimal value, out List<decimal> candidates)
    {
        candidates = [value];

        var cursor = end;

        while (cursor < text.Length && text[cursor] == ' ')
        {
            cursor++;
        }

        if (cursor < text.Length && text[cursor] == '%')
        {
            candidates.Add(value / 100m);
            return cursor + 1;
        }

        var remainder = text.AsSpan(cursor);

        if (remainder.StartsWith("percent", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(value / 100m);
            return cursor + "percent".Length;
        }

        foreach (var (suffix, multiplier) in ScaleSuffixes)
        {
            if (!remainder.StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A suffix only counts when the word ends there. Otherwise "3 monthly" reads as
            // three million, and the check starts accepting numbers nobody wrote.
            var afterSuffix = cursor + suffix.Length;

            if (afterSuffix < text.Length && char.IsAsciiLetter(text[afterSuffix]))
            {
                continue;
            }

            candidates.Add(value * multiplier);
            return afterSuffix;
        }

        return end;
    }
}
