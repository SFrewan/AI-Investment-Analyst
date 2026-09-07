using System.Globalization;
using System.Text.Json;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Admission;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Infrastructure.Admission;

/// <summary>One strategy as the register holds it, with what the register knows about its trials.</summary>
/// <param name="Event">The declaration itself, rebuilt from the register's fields.</param>
/// <param name="TrialsInFamily">
/// How many variants - including this one - have been measured within this strategy's search
/// family. The larger of what the entry declares and what the register itself contains, so a
/// declaration can only make its own budget position worse.
/// </param>
/// <param name="FamiliesOnThisEvidenceBase">
/// How many distinct search families the register declares against this evidence base. Counted
/// here rather than declared, so no entry can lower the significance correction it pays.
/// </param>
/// <param name="Note">Why this entry exists, in the register author's own words.</param>
public sealed record RegisteredStrategy(
    StrategyEvent Event,
    int TrialsInFamily,
    int FamiliesOnThisEvidenceBase,
    string Note);

/// <summary>
/// The declarations this installation has committed to, read from a file rather than from code.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this fixes.</strong> Both rehearsals used to build a declaration inline and date it
/// to the earliest prediction they were about to score, which made every backtest pass the
/// look-ahead check by construction. The check was still there; it just could not fail. Reading the
/// declaration from a committed file instead makes three things true that were not true before: the
/// event exists before the measurement runs, its date is whatever was recorded when it was first
/// written rather than whatever makes the run pass, and changing it leaves a diff.
/// </para>
/// <para>
/// <strong>The fingerprint check.</strong> An entry may carry the fingerprint its fields produce.
/// When it does, the register recomputes it and refuses the whole file on a mismatch. So a
/// threshold quietly nudged, a claim softened, or a backtest relabelled prospective stops the run
/// rather than changing the verdict. An entry with no fingerprint is accepted and its computed one
/// is reported, which is how a new declaration is bootstrapped: declare it, run once, paste the
/// fingerprint back, and from then on it is sealed.
/// </para>
/// <para>
/// <strong>What it still cannot do.</strong> Nothing here proves the event was not chosen after
/// someone looked at the outcomes. A file written today says today. What it does is stop the
/// pretence: a backtest declaration is dated honestly, is refused by the look-ahead check like any
/// other, and becomes the fixed point from which genuinely prospective predictions can accumulate.
/// </para>
/// </remarks>
public static class StrategyRegister
{
    /// <summary>Where the register lives, relative to the repository root.</summary>
    public const string RelativePath = "declarations/strategies.json";

    /// <summary>
    /// Reads the register, refusing any entry whose recorded fingerprint no longer matches.
    /// </summary>
    public static IReadOnlyDictionary<string, RegisteredStrategy> Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var read = new List<RegisteredStrategy>();

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new DomainValidationException(
                nameof(json),
                "The strategy register is a list of declarations. A register that is not a list " +
                "cannot be appended to without rewriting what is already in it.");
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            read.Add(Read(element));
        }

        if (read.Count == 0)
        {
            throw new DomainValidationException(
                nameof(json),
                "The strategy register is empty. A run that finds no declaration should say so " +
                "rather than invent one.");
        }

        // Both counts come from the file rather than from any entry's own say-so. That is what
        // keeps the search family from being a way out of the trial budget: naming a fresh family
        // does not reset anything a declaration controls, and it raises the significance correction
        // every strategy on that evidence base pays, including the one that named it.
        // Scoped to (family, evidence base) deliberately. Trying the same hypothesis again on a
        // different dataset is out-of-sample replication, which is the good kind of repetition, and
        // charging it as a further trial would penalise exactly what the platform wants more of.
        var inFamily = read
            .GroupBy(
                e => e.Event.SearchFamily + "|" + e.Event.EvidenceBaseFingerprint,
                StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var families = read
            .GroupBy(e => e.Event.EvidenceBaseFingerprint, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.Event.SearchFamily).Distinct(StringComparer.Ordinal).Count(),
                StringComparer.Ordinal);

        var entries = new Dictionary<string, RegisteredStrategy>(StringComparer.Ordinal);

        foreach (var entry in read)
        {
            var counted = inFamily[
                entry.Event.SearchFamily + "|" + entry.Event.EvidenceBaseFingerprint];

            var resolved = entry with
            {
                // The declared count exists because variants can be compared without ever being
                // written down - fifteen attribute columns in one table are fifteen comparisons and
                // one register entry. Whichever count is larger is the honest one.
                TrialsInFamily = Math.Max(entry.TrialsInFamily, counted),
                FamiliesOnThisEvidenceBase = families[entry.Event.EvidenceBaseFingerprint],
            };

            if (!entries.TryAdd(resolved.Event.Type.Value, resolved))
            {
                throw new DomainValidationException(
                    nameof(json),
                    $"'{resolved.Event.Type}' is declared twice in the register. Two declarations " +
                    "for one strategy means whichever is read first decides the bar, which is not a " +
                    "decision anybody took.");
            }
        }

        return entries;
    }

    /// <summary>The declaration for one strategy, or a refusal naming what is missing.</summary>
    public static RegisteredStrategy Require(
        IReadOnlyDictionary<string, RegisteredStrategy> register,
        string type)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        return register.TryGetValue(type, out var entry)
            ? entry
            : throw new DomainRuleViolationException(
                "Admission.StrategyNotDeclared",
                $"'{type}' is not in the strategy register. A strategy is declared before it is " +
                "measured, or the declaration is a description of the measurement.");
    }

    private static RegisteredStrategy Read(JsonElement element)
    {
        var type = OpportunityType.Create(Text(element, "type"));
        var direction = Enum.Parse<PredictionDirection>(Text(element, "direction"), ignoreCase: false);
        var basis = Enum.Parse<DeclarationBasis>(Text(element, "basis"), ignoreCase: false);
        var threshold = Percentage.FromRatio(Number(element, "thresholdRatio"));
        var horizon = Integer(element, "horizonSessions");
        var evidence = Text(element, "evidenceBaseFingerprint");
        var tuning = Text(element, "tuningBasisFingerprint");
        var declaredAt = Instant(element, "declaredAtUtc");
        var trials = Integer(element, "trialsInFamily");
        var family = Text(element, "searchFamily");
        var note = Text(element, "note");

        decimal? claimed = element.TryGetProperty("claimedBrier", out var claim) &&
                           claim.ValueKind == JsonValueKind.Number
            ? claim.GetDecimal()
            : null;

        decimal? skill = element.TryGetProperty("claimedSkill", out var share) &&
                         share.ValueKind == JsonValueKind.Number
            ? share.GetDecimal()
            : null;

        var declaration = StrategyEvent.Declare(
            type,
            direction,
            threshold,
            horizon,
            evidence,
            declaredAt,
            tuning,
            claimed,
            basis,
            skill,
            family);

        if (element.TryGetProperty("fingerprint", out var recorded) &&
            recorded.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(recorded.GetString()))
        {
            var expected = recorded.GetString()!.Trim();

            if (!string.Equals(expected, declaration.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainRuleViolationException(
                    "Admission.RegisterTampered",
                    $"'{type}' records fingerprint {expected} but its fields now produce " +
                    $"{declaration.Fingerprint}. Something in the declaration changed after it was " +
                    "sealed. Whichever edit that was, it is not the declaration the earlier " +
                    "measurements were taken against.");
            }
        }

        // The two counts are filled in by Parse from the whole file; these are placeholders.
        return new RegisteredStrategy(declaration, trials, 1, note);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new DomainValidationException(
                name,
                $"A register entry needs '{name}'. An entry missing it cannot be rebuilt into the " +
                "declaration it claims to record.");

    private static decimal Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : throw new DomainValidationException(name, $"A register entry needs a numeric '{name}'.");

    private static int Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : throw new DomainValidationException(name, $"A register entry needs an integer '{name}'.");

    private static DateTime Instant(JsonElement element, string name)
    {
        var text = Text(element, name);

        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : throw new DomainValidationException(
                name,
                $"'{text}' is not a timestamp this build can read.");
    }
}
