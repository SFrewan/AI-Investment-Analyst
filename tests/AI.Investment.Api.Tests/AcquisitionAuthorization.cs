using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The approved ceiling on requests leaving this platform, made into something that can refuse.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Every acquisition budget in this repository so far has lived
/// either in a constant inside a test class or in the sentence of an approval message. Neither can
/// stop a run: the first is edited by whoever is writing the run, and the second is not present at
/// the moment the request is built. The authorisation is therefore a declaration on disk, digested
/// so a later edit is visible, and a counter that the acquisition must ask before every dispatch.
/// </para>
/// <para>
/// <strong>What the ceiling is and is not.</strong> It is an internal dispatch ceiling: how many
/// requests this platform is permitted to send. It is <em>not</em> a statement about what the
/// vendor charges for them. This repository cannot prove vendor billing semantics - whether a
/// refused run costs nothing, whether two runs on one fingerprint bill once or twice - and nothing
/// here pretends otherwise.
/// </para>
/// <para>
/// <strong>What consumes it.</strong> Only a request that actually reaches the vendor. A request
/// suppressed by the ingestion-run ledger, refused by source admission, refused by the rate limiter
/// or denied by policy never leaves the process, and charging it against the ceiling would exhaust
/// an authorisation without fetching anything - the failure mode where a retried run runs out of
/// budget it never spent. A dispatched request consumes one whether it then succeeds or fails,
/// because the call was made.
/// </para>
/// </remarks>
internal sealed class AcquisitionAuthorization
{
    /// <summary>The original schema: one authorisation, standing alone.</summary>
    public const string Schema = "acquisition-authorization@1";

    /// <summary>
    /// The schema for an authorisation that replaces an earlier one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because two fields needed to become integrity-bound and could not be added to
    /// <see cref="Schema"/> without changing the digest of every declaration already approved under
    /// it. A second schema keeps the first one's canonical form byte-for-byte, so the price
    /// authorisation still loads exactly as it did on the day it was signed - which is the whole
    /// point of keeping it rather than amending it.
    /// </para>
    /// <para>
    /// A <c>@1</c> declaration may not carry the supersession fields at all. Allowing it to would
    /// be worse than not having them: the file would appear to name what it replaces and how much
    /// was already spent, while the digest covered neither, so both could be edited without
    /// detection.
    /// </para>
    /// </remarks>
    public const string SupersedingSchema = "acquisition-authorization@2";

    /// <summary>The two fields only a superseding authorisation may carry.</summary>
    private const string SupersedesProperty = "SupersedesAuthorizationId";

    private const string AlreadyConsumedProperty = "AlreadyConsumed";

    private readonly HashSet<string> _symbols;
    private readonly HashSet<string> _sources;
    private int _consumed;

    private AcquisitionAuthorization(
        string authorizationId,
        string evidenceBaseFingerprint,
        string vendor,
        HashSet<string> sources,
        HashSet<string> symbols,
        DateOnly windowFrom,
        DateOnly windowTo,
        int plannedRequests,
        int alreadySatisfied,
        int dispatchCeiling,
        string digest,
        string schema,
        string? supersedesAuthorizationId,
        int alreadyConsumed)
    {
        SchemaVersion = schema;
        SupersedesAuthorizationId = supersedesAuthorizationId;
        AlreadyConsumed = alreadyConsumed;
        AuthorizationId = authorizationId;
        EvidenceBaseFingerprint = evidenceBaseFingerprint;
        Vendor = vendor;
        _sources = sources;
        _symbols = symbols;
        WindowFrom = windowFrom;
        WindowTo = windowTo;
        PlannedRequests = plannedRequests;
        AlreadySatisfied = alreadySatisfied;
        DispatchCeiling = dispatchCeiling;
        Digest = digest;
    }

    public string AuthorizationId { get; }

    public string EvidenceBaseFingerprint { get; }

    public string Vendor { get; }

    public DateOnly WindowFrom { get; }

    public DateOnly WindowTo { get; }

    public int PlannedRequests { get; }

    public int AlreadySatisfied { get; }

    /// <summary>The maximum number of requests that may leave this platform under it.</summary>
    public int DispatchCeiling { get; }

    /// <summary>The digest recomputed from the file's own content at load.</summary>
    public string Digest { get; }

    /// <summary>Which schema this declaration was written against.</summary>
    public string SchemaVersion { get; }

    /// <summary>
    /// The authorisation this one replaces, or null when it replaces none.
    /// </summary>
    /// <remarks>
    /// Digest-covered. An edit to it invalidates the file, which is what makes the supersession
    /// chain evidence rather than commentary.
    /// </remarks>
    public string? SupersedesAuthorizationId { get; }

    /// <summary>
    /// What was already spent under the authorisation this one replaces.
    /// </summary>
    /// <remarks>
    /// <strong>Evidence, not a charge.</strong> It records history so the chain can be audited end
    /// to end; it is deliberately <em>not</em> subtracted from <see cref="DispatchCeiling"/>,
    /// because a superseding authorisation's ceiling is sized to the work that remains rather than
    /// inherited from the budget that preceded it. What a runner charges against this ceiling is
    /// what has been spent against <em>this</em> authorisation, and nothing else.
    /// </remarks>
    public int AlreadyConsumed { get; }

    public IReadOnlyCollection<string> Symbols => _symbols;

    public IReadOnlyCollection<string> Sources => _sources;

    public int Consumed => _consumed;

    public int Remaining => DispatchCeiling - _consumed;

    /// <summary>
    /// Reads the authorisation and refuses it unless it is internally consistent.
    /// </summary>
    /// <remarks>
    /// Every failure throws rather than returning a permissive default. An authorisation that
    /// cannot be read is not an authorisation for everything, and this is the one place in the
    /// acquisition path where that mistake would be unrecoverable.
    /// </remarks>
    public static AcquisitionAuthorization Load(string path, string expectedEvidenceBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEvidenceBase);

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var root = document.RootElement;
        var schema = Text(root, "Schema");
        var superseding = string.Equals(schema, SupersedingSchema, StringComparison.Ordinal);

        if (!superseding && !string.Equals(schema, Schema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The authorisation declares schema '{schema}', which this loader does not " +
                $"understand. Expected '{Schema}' or '{SupersedingSchema}'. Refused rather than " +
                "interpreted.");
        }

        var carriesSupersession =
            root.TryGetProperty(SupersedesProperty, out _) ||
            root.TryGetProperty(AlreadyConsumedProperty, out _);

        if (!superseding && carriesSupersession)
        {
            throw new InvalidOperationException(
                $"A '{Schema}' authorisation carries {SupersedesProperty} or " +
                $"{AlreadyConsumedProperty}, and that schema's digest does not cover them. A field " +
                "the digest cannot protect must not be present, because it would read as evidence " +
                $"while being editable. Declare '{SupersedingSchema}' instead. Refused.");
        }

        string? supersedes = null;
        var alreadyConsumed = 0;

        if (superseding)
        {
            supersedes = Text(root, SupersedesProperty);
            alreadyConsumed = root.GetProperty(AlreadyConsumedProperty).GetInt32();

            if (string.IsNullOrWhiteSpace(supersedes))
            {
                throw new InvalidOperationException(
                    $"A '{SupersedingSchema}' authorisation must name the authorisation it " +
                    "replaces. One that supersedes nothing is an original, not a successor.");
            }

            if (alreadyConsumed < 0)
            {
                throw new InvalidOperationException(
                    $"{AlreadyConsumedProperty} is {alreadyConsumed}. Spending cannot be negative, " +
                    "and an authorisation that claimed it was is describing a credit-back that this " +
                    "platform does not have.");
            }
        }

        var evidenceBase = Text(root, "EvidenceBaseFingerprint");

        if (!string.Equals(evidenceBase, expectedEvidenceBase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The authorisation is for evidence base '{evidenceBase}' and the universe in this " +
                $"repository is '{expectedEvidenceBase}'. An authorisation does not carry across a " +
                "reseal, because the members it names may no longer be the members.");
        }

        var sources = root.GetProperty(nameof(Sources))
            .EnumerateArray()
            .Select(s => s.GetString() ?? string.Empty)
            .ToList();

        var symbols = root.GetProperty(nameof(Symbols))
            .EnumerateArray()
            .Select(s => s.GetString() ?? string.Empty)
            .ToList();

        var vendor = Text(root, "Vendor");
        var from = Date(root, "WindowFromUtc");
        var to = Date(root, "WindowToUtc");
        var planned = root.GetProperty(nameof(PlannedRequests)).GetInt32();
        var satisfied = root.GetProperty(nameof(AlreadySatisfied)).GetInt32();
        var ceiling = root.GetProperty(nameof(DispatchCeiling)).GetInt32();
        var stated = Text(root, "AuthorizationDigest");

        var computed = ComputeDigest(
            schema, evidenceBase, vendor, sources, from, to, planned, satisfied, ceiling, symbols,
            supersedes, alreadyConsumed);

        if (!string.Equals(computed, stated, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The authorisation's digest does not match its content. Something changed after it " +
                $"was approved. Stated `{stated}`, computed `{computed}`. Refused.");
        }

        // Internal consistency, checked rather than trusted: the ceiling has to be what the
        // planned and already-satisfied counts imply, and the symbols have to be distinct.
        if (ceiling != planned - satisfied)
        {
            throw new InvalidOperationException(
                $"The ceiling of {ceiling} does not equal {planned} planned less {satisfied} " +
                "already satisfied. The arithmetic of an authorisation is not negotiable.");
        }

        var distinctSymbols = new HashSet<string>(symbols, StringComparer.Ordinal);
        var distinctSources = new HashSet<string>(sources, StringComparer.Ordinal);

        if (distinctSymbols.Count != symbols.Count || distinctSources.Count != sources.Count)
        {
            throw new InvalidOperationException(
                "The authorisation repeats a symbol or a source. A repeated entry would either " +
                "double-count the plan or hide one, and which is not decidable from the file.");
        }

        if (planned != distinctSymbols.Count * distinctSources.Count)
        {
            throw new InvalidOperationException(
                $"{planned} planned requests do not match {distinctSymbols.Count} symbols across " +
                $"{distinctSources.Count} sources. One of the two is wrong and this refuses both.");
        }

        return new AcquisitionAuthorization(
            Text(root, "AuthorizationId"),
            evidenceBase,
            vendor,
            distinctSources,
            distinctSymbols,
            from,
            to,
            planned,
            satisfied,
            ceiling,
            computed,
            schema,
            supersedes,
            alreadyConsumed);
    }

    /// <summary>
    /// The digest of a declaration's content, for writing one that will load.
    /// </summary>
    /// <remarks>
    /// Exposed so a stage that drafts a successor computes its digest with the same code the loader
    /// checks it against, rather than with a copy that can drift. Nothing here reads or writes a
    /// file: it is the canonical form and a hash, and a caller still has to persuade
    /// <see cref="Load"/> that the result is consistent.
    /// </remarks>
    public static string DigestFor(
        string schema,
        string evidenceBase,
        string vendor,
        IReadOnlyList<string> sources,
        DateOnly from,
        DateOnly to,
        int planned,
        int satisfied,
        int ceiling,
        IReadOnlyList<string> symbols,
        string? supersedesAuthorizationId,
        int alreadyConsumed) =>
        ComputeDigest(
            schema, evidenceBase, vendor, sources, from, to, planned, satisfied, ceiling, symbols,
            supersedesAuthorizationId, alreadyConsumed);

    /// <summary>
    /// Whether this exact request is one the authorisation covers, before any counting.
    /// </summary>
    /// <remarks>
    /// Scope first, ceiling second, and in that order for a reason: a request for the wrong symbol
    /// is not "within budget" even when the counter has room, and answering it in terms of the
    /// counter would let an unauthorised fetch look like an authorised one that happened to fit.
    /// </remarks>
    public Verdict Covers(string source, string symbol, DateOnly from, DateOnly to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (!_sources.Contains(source))
        {
            return new Verdict(false, Inv($"source `{source}` is not authorised; only {string.Join(" and ", _sources.Order(StringComparer.Ordinal))} are"));
        }

        if (!_symbols.Contains(symbol))
        {
            return new Verdict(false, Inv($"symbol `{symbol}` is not in the {_symbols.Count} this authorisation names"));
        }

        if (from != WindowFrom || to != WindowTo)
        {
            return new Verdict(false, Inv($"window {from:yyyy-MM-dd}..{to:yyyy-MM-dd} is not the authorised {WindowFrom:yyyy-MM-dd}..{WindowTo:yyyy-MM-dd}"));
        }

        return new Verdict(true, null);
    }

    /// <summary>
    /// Claims one dispatch. Returns false when the ceiling is reached or the request is out of scope.
    /// </summary>
    /// <remarks>
    /// Call this immediately before the request leaves, and only for requests that will leave. A
    /// suppressed or refused request must not be passed here - see <see cref="Suppressed"/>, which
    /// records that it happened and consumes nothing.
    /// </remarks>
    public Verdict TryConsume(string source, string symbol, DateOnly from, DateOnly to)
    {
        var covered = Covers(source, symbol, from, to);

        if (!covered.Allowed)
        {
            return covered;
        }

        if (_consumed >= DispatchCeiling)
        {
            return new Verdict(
                false,
                Inv($"the authorised ceiling of {DispatchCeiling} dispatches is reached; {_consumed} have been made and this one is refused"));
        }

        _consumed++;

        return new Verdict(true, null);
    }

    /// <summary>Records a request that never left the process. Consumes nothing, by design.</summary>
    public int Suppressed { get; private set; }

    /// <summary>Notes a request the ledger, admission, the rate limiter or policy stopped.</summary>
    public void RecordSuppressed() => Suppressed++;

    /// <summary>
    /// Charges dispatches an earlier process already made against this same authorisation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The ceiling belongs to the authorisation, not to a process.</strong> A run split into
    /// batches loads this declaration afresh each time, with a counter at zero. Without this, three
    /// batches of seventy would each believe they had the full ceiling left, and the approved total
    /// would be spendable once per batch - which is the failure the artefact exists to prevent. What
    /// earlier processes spent is read from the outcome artefacts they wrote and charged here, before
    /// anything is dispatched.
    /// </para>
    /// <para>
    /// <strong>It only ever removes headroom.</strong> There is deliberately no way to give any back:
    /// an authorisation that can be credited is one that can be reset, and a reset ceiling is not a
    /// ceiling. Over-spending is refused rather than clamped, because a clamp would quietly turn a
    /// bookkeeping error into a permission.
    /// </para>
    /// </remarks>
    public void RecordPriorConsumption(int dispatches)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dispatches);

        if (_consumed + dispatches > DispatchCeiling)
        {
            throw new InvalidOperationException(
                $"{_consumed + dispatches} dispatches have already been made against a ceiling of " +
                $"{DispatchCeiling}. An authorisation cannot be over-spent retrospectively, and this " +
                "is refused rather than clamped.");
        }

        _consumed += dispatches;
    }

    private static string ComputeDigest(
        string schema,
        string evidenceBase,
        string vendor,
        IReadOnlyList<string> sources,
        DateOnly from,
        DateOnly to,
        int planned,
        int satisfied,
        int ceiling,
        IReadOnlyList<string> symbols,
        string? supersedesAuthorizationId,
        int alreadyConsumed)
    {
        var canonical = new StringBuilder();

        // The schema itself leads the canonical form, so a '@1' file and a '@2' file can never
        // hash alike and the '@1' form is byte-for-byte what it always was.
        canonical.Append(schema).Append('\n');
        canonical.Append(evidenceBase).Append('\n');
        canonical.Append(vendor).Append('\n');
        canonical.Append(string.Join(",", sources)).Append('\n');
        canonical.Append(Inv($"{from:yyyy-MM-dd}")).Append('\n');
        canonical.Append(Inv($"{to:yyyy-MM-dd}")).Append('\n');
        canonical.Append(planned.ToString(CultureInfo.InvariantCulture)).Append('\n');
        canonical.Append(satisfied.ToString(CultureInfo.InvariantCulture)).Append('\n');
        canonical.Append(ceiling.ToString(CultureInfo.InvariantCulture));

        foreach (var symbol in symbols)
        {
            canonical.Append('\n').Append(symbol);
        }

        // Appended last and only when present, so nothing about the '@1' canonical form moves.
        if (supersedesAuthorizationId is not null)
        {
            canonical.Append('\n').Append(supersedesAuthorizationId);
            canonical.Append('\n').Append(alreadyConsumed.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static string Text(JsonElement root, string property) =>
        root.GetProperty(property).GetString() ?? string.Empty;

    private static DateOnly Date(JsonElement root, string property) =>
        DateOnly.ParseExact(Text(root, property), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Inv(FormattableString text) =>
        text.ToString(CultureInfo.InvariantCulture);

    /// <param name="Allowed">Whether this request may leave the platform.</param>
    /// <param name="Reason">Why not, when not. Null exactly when allowed.</param>
    internal sealed record Verdict(bool Allowed, string? Reason);
}
