using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Validation;

/// <summary>The rule a benchmark follows. Deliberately few, and deliberately dull.</summary>
public enum BenchmarkRule
{
    Unknown = 0,

    /// <summary>Buy at the first admissible price in the window, hold, sell at the last. Nothing else.</summary>
    BuyAndHold = 1,
}

/// <summary>
/// The naive benchmark, fixed in advance and provable afterwards.
/// </summary>
/// <remarks>
/// <para>
/// The benchmark is the honest comparison, and it is only honest if it was chosen before the result
/// was known. Nothing in code can stop somebody editing a definition and re-running, so this type
/// does the next best thing: it records when the definition was declared, refuses to take part in a
/// run that started before that moment, and publishes a fingerprint over its own fields. A report
/// carries the fingerprint, so a later reader can check that the benchmark described is the benchmark
/// used, and a changed definition produces a visibly different report rather than a quietly better
/// one.
/// </para>
/// <para>
/// <strong>Comparable assumptions, or the comparison is theatre.</strong> The benchmark and the
/// strategy share the same window, the same starting capital and the same cost model, and the returns
/// of both are computed by the same function. A benchmark charged costs the strategy does not pay -
/// or the reverse - measures the accounting rather than the system.
/// </para>
/// <para>
/// Buy-and-hold of a broad index is the right naive comparison precisely because it is so hard to
/// beat and requires no skill whatsoever. If the platform cannot beat it, that is the finding.
/// </para>
/// </remarks>
public sealed record BenchmarkDefinition
{
    public const int MaxNameLength = 120;

    public const string DeclaredAfterTheRunRule = "Validation.BenchmarkDeclaredAfterTheRun";

    private BenchmarkDefinition(
        string name,
        IngestionSubject subject,
        string priceAttribute,
        BenchmarkRule rule,
        Money initialCapital,
        Percentage costPerTrade,
        DateTime declaredAtUtc)
    {
        Name = name;
        Subject = subject;
        PriceAttribute = priceAttribute;
        Rule = rule;
        InitialCapital = initialCapital;
        CostPerTrade = costPerTrade;
        DeclaredAtUtc = declaredAtUtc;
    }

    public string Name { get; }

    /// <summary>What is bought and held - the index proxy.</summary>
    public IngestionSubject Subject { get; }

    /// <summary>The observation attribute the price is read from.</summary>
    public string PriceAttribute { get; }

    public BenchmarkRule Rule { get; }

    public Money InitialCapital { get; }

    /// <summary>Charged on entry and on exit, and charged identically to the strategy.</summary>
    public Percentage CostPerTrade { get; }

    /// <summary>When this definition was fixed. A run that began before it is refused.</summary>
    public DateTime DeclaredAtUtc { get; }

    /// <summary>
    /// A fingerprint over every field above, so a report can prove which definition it used.
    /// </summary>
    public string Fingerprint => Fingerprints.Of(Canonical());

    public static BenchmarkDefinition Create(
        string name,
        IngestionSubject subject,
        string priceAttribute,
        BenchmarkRule rule,
        Money initialCapital,
        Percentage costPerTrade,
        DateTime declaredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(initialCapital);
        ArgumentNullException.ThrowIfNull(costPerTrade);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(priceAttribute);
        DateRange.EnsureUtc(declaredAtUtc, nameof(declaredAtUtc));

        if (rule == BenchmarkRule.Unknown)
        {
            throw new DomainValidationException(
                nameof(rule),
                "A benchmark with no rule is not a benchmark. An unspecified one would be settled " +
                "later, which is exactly what must not happen.");
        }

        if (!subject.IsSpecific)
        {
            throw new DomainValidationException(
                nameof(subject),
                "A benchmark must name the instrument it holds. 'the market' is not a position.");
        }

        if (!initialCapital.IsPositive)
        {
            throw new DomainValidationException(
                nameof(initialCapital),
                "A benchmark starting with nothing returns nothing, whatever the market does.");
        }

        if (costPerTrade.Ratio < 0m)
        {
            throw new DomainValidationException(
                nameof(costPerTrade),
                "A negative trading cost is a subsidy, and would flatter whichever side received it.");
        }

        return new BenchmarkDefinition(
            name.Trim()[..Math.Min(name.Trim().Length, MaxNameLength)],
            subject,
            priceAttribute.Trim(),
            rule,
            initialCapital,
            costPerTrade,
            declaredAtUtc);
    }

    /// <summary>Refuses a run that started before this definition existed.</summary>
    public void EnsureDeclaredBefore(DateTime runStartedAtUtc)
    {
        DateRange.EnsureUtc(runStartedAtUtc, nameof(runStartedAtUtc));

        if (DeclaredAtUtc > runStartedAtUtc)
        {
            throw new DomainRuleViolationException(
                DeclaredAfterTheRunRule,
                $"the benchmark was declared at {DeclaredAtUtc:O}, after the run began at " +
                $"{runStartedAtUtc:O}. A benchmark chosen once the numbers are in is not a benchmark.");
        }
    }

    public override string ToString() => $"{Name} ({Rule} {Subject}) #{Fingerprint[..8]}";

    private string Canonical() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Name}|{Subject}|{PriceAttribute}|{Rule}|{InitialCapital.Amount}|" +
            $"{InitialCapital.Currency}|{CostPerTrade.Ratio}|{DeclaredAtUtc:O}");
}

/// <summary>Deterministic fingerprints, so a definition can be shown to be unchanged.</summary>
internal static class Fingerprints
{
    public static string Of(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}
