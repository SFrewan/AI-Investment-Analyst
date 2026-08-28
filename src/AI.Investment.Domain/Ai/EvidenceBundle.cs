using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Domain.Ai;

/// <summary>
/// Everything an agent is allowed to see about one subject at one moment, frozen and hashed.
/// </summary>
/// <remarks>
/// <para>
/// The bundle is the boundary between the part of the system that knows things and the part that
/// guesses. Three properties make it worth having, and all three are enforced here rather than
/// hoped for:
/// </para>
/// <list type="number">
/// <item><strong>It admits no judgement.</strong> Only observed facts and deterministic
/// calculations may enter. Feeding one agent's opinion to the next is how a single invented figure
/// becomes an apparent consensus, and no amount of downstream validation recovers from it.</item>
/// <item><strong>It cannot see the future.</strong> Every claim must have been published at or
/// before the cutoff. In live operation the cutoff is now and this costs nothing; in a backtest it
/// is the whole point.</item>
/// <item><strong>It is content-addressed.</strong> <see cref="Hash"/> is computed from what the
/// evidence says, not from the identities its claims happened to be given in memory, so the same
/// underlying data produces the same hash on a later run. That is what lets a stored analysis be
/// re-derived and compared rather than merely re-read.</item>
/// </list>
/// <para>
/// The hash is a fingerprint for comparison and audit, not a security control. It answers "is this
/// the same evidence as last time?", which is the question the validation phase depends on.
/// </para>
/// </remarks>
public sealed class EvidenceBundle
{
    /// <summary>
    /// The short label a prompt uses to refer to an item - <c>C1</c>, <c>C2</c>.
    /// </summary>
    /// <remarks>
    /// An agent has to cite the evidence it used, and a 36-character GUID is a poor thing to ask a
    /// language model to copy: it costs tokens, it is easy to corrupt by one character, and a
    /// corrupted citation is indistinguishable from an invented one. A short positional label is
    /// cheap to emit and cheap to verify, and it is stable because the bundle's order is
    /// content-sorted rather than insertion-ordered.
    /// </remarks>
    public const string LabelPrefix = "C";

    private readonly List<EvidenceItem> _items;
    private readonly List<Claim> _claims;

    private EvidenceBundle(
        IngestionSubject subject,
        KnowledgeCutoff cutoff,
        List<EvidenceItem> items,
        string hash)
    {
        Subject = subject;
        Cutoff = cutoff;
        _items = items;
        _claims = items.Select(item => item.Claim).ToList();
        Hash = hash;
    }

    public IngestionSubject Subject { get; }

    public KnowledgeCutoff Cutoff { get; }

    /// <summary>The evidence, in a stable content order.</summary>
    public IReadOnlyList<EvidenceItem> Items => _items;

    /// <summary>The claims behind those items: facts and calculations only.</summary>
    public IReadOnlyList<Claim> Claims => _claims;

    /// <summary>A content fingerprint of the whole bundle, as lower-case hexadecimal SHA-256.</summary>
    public string Hash { get; }

    public int Count => _items.Count;

    public static EvidenceBundle Create(
        IngestionSubject subject,
        KnowledgeCutoff cutoff,
        IEnumerable<EvidenceItem> items)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(cutoff);
        ArgumentNullException.ThrowIfNull(items);

        var materialised = items.Distinct().ToList();

        if (materialised.Count == 0)
        {
            throw new DomainRuleViolationException(
                "EvidenceBundle.Empty",
                "An evidence bundle may not be empty. An agent given nothing to read will answer " +
                "from the model's memory, which is exactly the failure this platform exists to " +
                "prevent.");
        }

        foreach (var item in materialised)
        {
            if (item.Claim.IsJudgement)
            {
                throw new DomainRuleViolationException(
                    "EvidenceBundle.JudgementIsNotEvidence",
                    $"A {item.Claim.Kind} may not enter an evidence bundle. Feeding one agent's " +
                    "opinion to the next is how a single invented figure becomes an apparent consensus.");
            }

            if (!cutoff.Admits(item.Claim.Provenance))
            {
                throw new DomainRuleViolationException(
                    "EvidenceBundle.LookAhead",
                    $"Evidence published at {item.Claim.Provenance.PublishedAtUtc:O} is not admissible " +
                    $"at a cutoff of {cutoff}. Admissibility is judged on publication, never on when " +
                    "this system happened to fetch it.");
            }
        }

        var ordered = materialised
            .OrderBy(Fingerprint, StringComparer.Ordinal)
            .ToList();

        return new EvidenceBundle(subject, cutoff, ordered, ComputeHash(subject, cutoff, ordered));
    }

    /// <summary>The label for the item at <paramref name="index"/>.</summary>
    public static string LabelAt(int index) =>
        LabelPrefix + (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The label this bundle uses for <paramref name="item"/>, or null if it is not in it.</summary>
    public string? LabelOf(EvidenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var index = _items.IndexOf(item);

        return index < 0 ? null : LabelAt(index);
    }

    /// <summary>Resolves a label an agent cited back to the item it names.</summary>
    public bool TryResolveLabel(string? label, out EvidenceItem? item)
    {
        item = null;

        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var trimmed = label.Trim();

        if (!trimmed.StartsWith(LabelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(
                trimmed.AsSpan(LabelPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var ordinal))
        {
            return false;
        }

        if (ordinal < 1 || ordinal > _items.Count)
        {
            return false;
        }

        item = _items[ordinal - 1];

        return true;
    }

    /// <summary>True when <paramref name="claimId"/> names a claim this bundle contains.</summary>
    public bool Contains(ClaimId claimId) => _claims.Exists(claim => claim.Id.Equals(claimId));

    /// <summary>The claims that carry a numeric value, which is what groundedness is checked against.</summary>
    public List<Claim> NumericClaims() =>
        _claims.Where(claim => TryReadNumber(claim, out _)).ToList();

    /// <summary>
    /// Reads a claim's value as a number, when it is one.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: the numeric CLR types a claim can legitimately hold, converted through
    /// <see cref="decimal"/> so that no binary floating-point rounding enters the comparison a
    /// groundedness check depends on.
    /// </remarks>
    public static bool TryReadNumber(Claim claim, out decimal value)
    {
        ArgumentNullException.ThrowIfNull(claim);

        switch (claim.UntypedValue)
        {
            case decimal d:
                value = d;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            default:
                value = 0m;
                return false;
        }
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Subject} @ {Cutoff}: {Count} items [{Hash[..12]}]");

    /// <summary>
    /// What a piece of evidence says, independent of the identity it was given in memory.
    /// </summary>
    /// <remarks>
    /// The in-memory <c>ClaimId</c> is a fresh GUID on every construction, so hashing it would make
    /// two bundles assembled from identical stored data compare as different - which would defeat
    /// the only question the hash exists to answer.
    /// </remarks>
    private static string Fingerprint(EvidenceItem item) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{item.Name}|{item.Claim.Kind}|{item.Claim.Provenance.SourceId}|" +
            $"{item.Claim.Provenance.SourceRecordId}|{item.Claim.Provenance.AsOfUtc:O}|" +
            $"{item.Claim.Provenance.PublishedAtUtc:O}|{item.Claim.ValueTypeName}|" +
            $"{FormatValue(item.Claim.UntypedValue)}");

    private static string FormatValue(object? value) =>
        value switch
        {
            null => string.Empty,
            decimal d => d.ToString("G29", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static string ComputeHash(
        IngestionSubject subject,
        KnowledgeCutoff cutoff,
        List<EvidenceItem> ordered)
    {
        var canonical = new StringBuilder();
        canonical.Append(subject).Append('\n').Append(cutoff).Append('\n');

        foreach (var item in ordered)
        {
            canonical.Append(Fingerprint(item)).Append('\n');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));

        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
