using System.Globalization;
using System.Text.Json;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Opportunities.Equity;

/// <summary>
/// The first concrete opportunity type: a position in a listed equity.
/// </summary>
/// <remarks>
/// <para>
/// It exists to prove the generic core is genuinely generic. The lifecycle, approval flow, limit
/// engine, capital ledger and audit trail in this phase know nothing about equities; everything
/// specific to one lives behind the three interfaces named in the architecture, and this type
/// implements two of them. A second type is those two classes again and no change here.
/// </para>
/// <para>
/// <strong>The detail payload is the only equity-shaped thing in the system.</strong> Nothing the
/// policy engine or the limit engine reads comes from it - they read the strongly typed core - so a
/// malformed payload can make an opportunity unusable but cannot make an unsafe one.
/// </para>
/// </remarks>
public static class EquityOpportunity
{
    public const string TypeName = "equity";

    public static OpportunityType Type { get; } = OpportunityType.Create(TypeName);
}

/// <summary>
/// The equity detail payload, read from the opportunity's JSON.
/// </summary>
/// <remarks>
/// Parsed with <see cref="JsonDocument"/> rather than a reflection-based deserializer, so the
/// domain keeps its property of depending on nothing that a trimmer or a source generator has an
/// opinion about, and so every field's absence produces a named reason rather than a default.
/// </remarks>
public sealed record EquityDetail
{
    private EquityDetail(
        string instrument,
        decimal quantity,
        decimal entryPrice,
        decimal targetPrice,
        string currencyCode,
        decimal successProbability,
        int horizonDays)
    {
        Instrument = instrument;
        Quantity = quantity;
        EntryPrice = entryPrice;
        TargetPrice = targetPrice;
        CurrencyCode = currencyCode;
        SuccessProbability = successProbability;
        HorizonDays = horizonDays;
    }

    public string Instrument { get; }

    public decimal Quantity { get; }

    public decimal EntryPrice { get; }

    public decimal TargetPrice { get; }

    public string CurrencyCode { get; }

    public decimal SuccessProbability { get; }

    public int HorizonDays { get; }

    /// <summary>Writes a payload in the shape this type reads.</summary>
    public static string ToJson(
        string instrument,
        decimal quantity,
        decimal entryPrice,
        decimal targetPrice,
        string currencyCode,
        decimal successProbability,
        int horizonDays)
    {
        var culture = CultureInfo.InvariantCulture;

        return string.Concat(
            "{\"instrument\":\"", instrument,
            "\",\"quantity\":", quantity.ToString(culture),
            ",\"entryPrice\":", entryPrice.ToString(culture),
            ",\"targetPrice\":", targetPrice.ToString(culture),
            ",\"currency\":\"", currencyCode,
            "\",\"successProbability\":", successProbability.ToString(culture),
            ",\"horizonDays\":", horizonDays.ToString(culture),
            "}");
    }

    /// <summary>Reads the payload, or throws with the first thing wrong with it.</summary>
    public static EquityDetail Parse(OpportunityDetail detail)
    {
        var problems = TryParse(detail, out var parsed);

        return problems.Count == 0 && parsed is not null
            ? parsed
            : throw new DomainValidationException(nameof(detail), string.Join(" ", problems));
    }

    /// <summary>
    /// Reads the payload, reporting every problem rather than throwing on the first.
    /// </summary>
    /// <returns>An empty list when the payload is usable.</returns>
    public static IReadOnlyList<string> TryParse(OpportunityDetail detail, out EquityDetail? parsed)
    {
        ArgumentNullException.ThrowIfNull(detail);

        parsed = null;

        var problems = new List<string>();

        using var document = JsonDocument.Parse(detail.Json);
        var root = document.RootElement;

        var instrument = ReadString(root, "instrument", problems);
        var currency = ReadString(root, "currency", problems);
        var quantity = ReadDecimal(root, "quantity", problems);
        var entryPrice = ReadDecimal(root, "entryPrice", problems);
        var targetPrice = ReadDecimal(root, "targetPrice", problems);
        var probability = ReadDecimal(root, "successProbability", problems);
        var horizon = ReadDecimal(root, "horizonDays", problems);

        if (quantity is <= 0m)
        {
            problems.Add("'quantity' must be positive; direction is the order's side, not its sign.");
        }

        if (entryPrice is <= 0m)
        {
            problems.Add("'entryPrice' must be positive.");
        }

        if (targetPrice is < 0m)
        {
            problems.Add("'targetPrice' may not be negative.");
        }

        if (probability is < 0m or > 1m)
        {
            problems.Add("'successProbability' must be between 0 and 1; anything else is not a probability.");
        }

        if (horizon is <= 0m or > 3650m)
        {
            problems.Add("'horizonDays' must be between 1 and 3650.");
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        parsed = new EquityDetail(
            instrument!,
            quantity!.Value,
            entryPrice!.Value,
            targetPrice!.Value,
            currency!,
            probability!.Value,
            (int)horizon!.Value);

        return [];
    }

    private static string? ReadString(JsonElement root, string name, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            problems.Add($"'{name}' is missing or is not a string.");

            return null;
        }

        var value = element.GetString();

        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"'{name}' is empty.");

            return null;
        }

        return value.Trim();
    }

    private static decimal? ReadDecimal(JsonElement root, string name, List<string> problems)
    {
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetDecimal(out var value))
        {
            problems.Add($"'{name}' is missing or is not a number.");

            return null;
        }

        return value;
    }
}
