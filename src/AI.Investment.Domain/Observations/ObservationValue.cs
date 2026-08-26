using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Observations;

/// <summary>
/// An observed value in the canonical form the platform stores.
/// </summary>
/// <remarks>
/// <para>
/// One kind and one culture-invariant string. Storing numbers as text is a deliberate trade: a
/// canonical <c>decimal</c> round-trip loses nothing, and it keeps every observation in one column
/// regardless of type - which is what lets a single table hold a company name, a revenue figure, a
/// flag and a filing date without a column per normaliser.
/// </para>
/// <para>
/// <strong>Culture-invariant throughout.</strong> A value parsed under one locale and read back
/// under another is the kind of defect that appears only on someone else's machine, months later,
/// as a revenue figure a thousand times too large.
/// </para>
/// </remarks>
public sealed record ObservationValue
{
    public const int MaxTextLength = 4000;

    private ObservationValue(ObservationValueKind kind, string canonical)
    {
        Kind = kind;
        Canonical = canonical;
    }

    public ObservationValueKind Kind { get; }

    /// <summary>The value, culture-invariant.</summary>
    public string Canonical { get; }

    public static ObservationValue Text(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(
                nameof(value),
                "An observation must have a value. A blank one is an absence, and an absence is not " +
                "something to record as observed.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxTextLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"An observed text value may not exceed {MaxTextLength} characters.");
        }

        return new ObservationValue(ObservationValueKind.Text, trimmed);
    }

    public static ObservationValue Number(decimal value) =>
        new(ObservationValueKind.Number, value.ToString("G29", CultureInfo.InvariantCulture));

    public static ObservationValue Boolean(bool value) =>
        new(ObservationValueKind.Boolean, value ? "true" : "false");

    public static ObservationValue Timestamp(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new DomainValidationException(
                nameof(value),
                "An observed timestamp must be UTC. A local time stored without its offset is a " +
                "number nobody can interpret later.");
        }

        return new ObservationValue(
            ObservationValueKind.Timestamp,
            value.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Rebuilds a value from its stored form.</summary>
    public static ObservationValue Restore(ObservationValueKind kind, string canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical))
        {
            throw new DomainValidationException(nameof(canonical), "A stored observation has no value.");
        }

        return kind switch
        {
            ObservationValueKind.Text => Text(canonical),
            ObservationValueKind.Number => Number(ParseNumber(canonical)),
            ObservationValueKind.Boolean => Boolean(ParseBoolean(canonical)),
            ObservationValueKind.Timestamp => Timestamp(ParseTimestamp(canonical)),

            // Unknown, and any kind a future build adds that this one does not recognise.
            // Refused rather than guessed: a value whose type is uncertain is not a value.
            _ => throw new DomainValidationException(
                nameof(kind),
                $"'{kind}' is not an observation value kind this build can restore."),
        };
    }

    public decimal AsNumber() =>
        Kind == ObservationValueKind.Number
            ? ParseNumber(Canonical)
            : throw new DomainRuleViolationException(
                "ObservationValue.NotANumber",
                $"This observation is {Kind}, not a number. Reading it as one would invent a figure.");

    public bool AsBoolean() =>
        Kind == ObservationValueKind.Boolean
            ? ParseBoolean(Canonical)
            : throw new DomainRuleViolationException(
                "ObservationValue.NotABoolean",
                $"This observation is {Kind}, not a boolean.");

    public DateTime AsTimestamp() =>
        Kind == ObservationValueKind.Timestamp
            ? ParseTimestamp(Canonical)
            : throw new DomainRuleViolationException(
                "ObservationValue.NotATimestamp",
                $"This observation is {Kind}, not a timestamp.");

    public override string ToString() => $"{Canonical} ({Kind})";

    private static decimal ParseNumber(string canonical) =>
        decimal.TryParse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new DomainValidationException(
                nameof(canonical),
                $"'{canonical}' is not a number this build can read.");

    private static bool ParseBoolean(string canonical) =>
        bool.TryParse(canonical, out var value)
            ? value
            : throw new DomainValidationException(
                nameof(canonical),
                $"'{canonical}' is not a boolean.");

    private static DateTime ParseTimestamp(string canonical) =>
        DateTime.TryParse(
            canonical,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : throw new DomainValidationException(
                nameof(canonical),
                $"'{canonical}' is not a round-trippable timestamp.");
}
