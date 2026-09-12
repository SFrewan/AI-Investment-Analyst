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

    /// <summary>
    /// The schema for an authorisation bound to one sealed declaration, with an expiry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A third schema, for the third time the same reason applied.</strong> Nine fields had
    /// to become integrity-bound - the declaration this authorisation is for, the category and
    /// subject kind it is for, and the instants it is valid between - and none of them could be
    /// added to <see cref="Schema"/> or <see cref="SupersedingSchema"/> without changing the digest
    /// of every declaration already approved under them. So the <c>@1</c> prefix is emitted exactly
    /// as it always was, and <c>@3</c> appends its own tail.
    /// </para>
    /// <para>
    /// <strong>What <c>@3</c> adds that mattered enough to version for.</strong> A <c>@1</c> or
    /// <c>@2</c> authorisation names an evidence base and a list of subjects, and nothing else ties
    /// it to the document that decided its scope. A <c>@3</c> authorisation names the sealed
    /// declaration it was signed against by path, by whole-file SHA-256 and by that declaration's
    /// own internal digest - all three inside the canonical form, and all three recomputed from the
    /// file on disk when it loads. An authorisation that states a hash nobody recomputes is a
    /// comment.
    /// </para>
    /// <para>
    /// <strong>And it expires.</strong> <c>@1</c> and <c>@2</c> are bounded only by their ceiling
    /// and by the approval that raises a batch flag. That leaves an authorisation which is sealed,
    /// approved and simply never run spendable indefinitely, against a declaration whose evidence
    /// base may have moved. <see cref="ExpiresAtUtc"/> closes that, and it is a backstop rather
    /// than the primary control: the batch approval still governs what actually runs.
    /// </para>
    /// </remarks>
    public const string DocumentScopedSchema = "acquisition-authorization@3";

    /// <summary>
    /// The deterministic reason an expired authorisation refuses. Not an exception type.
    /// </summary>
    /// <remarks>
    /// Named as the authorisation's own rule rather than a runner's, because the authorisation is
    /// what expired. A runner wraps it in its own versioned rule id when it refuses, exactly as it
    /// already wraps <see cref="Covers"/>'s reason in <c>runner.authorisation-covers@1</c>.
    /// </remarks>
    public const string ExpiredRule = "authorization-expired@1";

    /// <summary>The reason an authorisation refuses before its own validity has begun.</summary>
    public const string NotYetValidRule = "authorization-not-yet-valid@1";

    /// <summary>
    /// The longest an authorisation may be valid for. A clamp in code, not a suggestion in a file.
    /// </summary>
    /// <remarks>
    /// The same shape as <c>SecEdgarOptions.FairAccessRequestsPerSecond</c>: a hard bound here and
    /// a lower value in the declaration. It makes "do not create a permanent authorisation"
    /// something the loader enforces rather than something a reviewer has to notice.
    /// </remarks>
    public const int MaximumLifetimeDays = 30;

    /// <summary>The instant format every <c>@3</c> field uses, and the only one accepted.</summary>
    public const string InstantFormat = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>The two fields only a superseding authorisation may carry.</summary>
    private const string SupersedesProperty = "SupersedesAuthorizationId";

    private const string AlreadyConsumedProperty = "AlreadyConsumed";

    /// <summary>
    /// The digest of the authorisation this one replaces.
    /// </summary>
    /// <remarks>
    /// <strong>Covered by the canonical form in <c>@3</c>, and deliberately not before.</strong> It
    /// is present in the <c>@2</c> declarations on disk, is not read by this loader and is not in
    /// <c>@2</c>'s canonical form - so there it reads as a cryptographic binding to the predecessor
    /// while protecting nothing, and could be edited to name any digest without invalidating the
    /// seal. Bringing it into <c>@3</c>'s canonical form fixes that for new authorisations without
    /// touching the digest of a single existing one.
    /// </remarks>
    private const string SupersedesDigestProperty = "SupersedesAuthorizationDigest";

    /// <summary>Every field a <c>@3</c> authorisation must carry. All nine are digest-covered.</summary>
    private static readonly string[] DocumentScopedProperties =
    [
        "DataCategory",
        "SubjectKind",
        "DeclarationPath",
        "DeclarationSchema",
        "DeclarationDigestField",
        "DeclarationDigest",
        "DeclarationSha256",
        "IssuedAtUtc",
        "ExpiresAtUtc",
    ];

    /// <summary>
    /// The subset an older schema may not carry, because there they would read as a guarantee.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not all nine, and the difference is what the field would claim.</strong> A binding
    /// or a validity instant on a <c>@1</c> file reads as a cryptographic tie to a document, or as
    /// a bound on when the authorisation may act, while the digest protects neither - which is the
    /// <see cref="SupersedesDigestProperty"/> mistake exactly, and the one this refuses to repeat.
    /// </para>
    /// <para>
    /// <strong><c>DataCategory</c> and <c>SubjectKind</c> are deliberately absent from this
    /// list.</strong> Four of the five authorisations already on disk carry <c>SubjectKind</c> as
    /// descriptive prose, and nothing reads it there: with no <see cref="Scope"/>, an older
    /// authorisation has no category or subject-kind check for the field to weaken. Refusing them
    /// would invalidate four approved declarations to tidy a field that claims nothing, which is
    /// precisely the retroactive breakage a new schema version exists to avoid. In <c>@3</c> both
    /// become digest-covered and both are enforced.
    /// </para>
    /// </remarks>
    private static readonly string[] ProtectedDocumentScopedProperties =
    [
        "DeclarationPath",
        "DeclarationSchema",
        "DeclarationDigestField",
        "DeclarationDigest",
        "DeclarationSha256",
        "IssuedAtUtc",
        "ExpiresAtUtc",
    ];

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
        int alreadyConsumed,
        string? supersedesAuthorizationDigest,
        DocumentScope? documentScope)
    {
        SchemaVersion = schema;
        SupersedesAuthorizationId = supersedesAuthorizationId;
        AlreadyConsumed = alreadyConsumed;
        SupersedesAuthorizationDigest = supersedesAuthorizationDigest;
        Scope = documentScope;
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
    /// The declaration binding, category, subject kind and validity span. Null before <c>@3</c>.
    /// </summary>
    /// <remarks>
    /// Null is the honest representation for <c>@1</c> and <c>@2</c>: those authorisations do not
    /// have a bound declaration or an expiry, and inventing a default for them would make an
    /// unbounded authorisation look bounded.
    /// </remarks>
    public DocumentScope? Scope { get; }

    /// <summary>Whether this authorisation carries a declaration binding and an expiry.</summary>
    public bool IsDocumentScoped => Scope is not null;

    /// <summary>
    /// The authorisation this one replaces, or null when it replaces none.
    /// </summary>
    /// <remarks>
    /// Digest-covered. An edit to it invalidates the file, which is what makes the supersession
    /// chain evidence rather than commentary.
    /// </remarks>
    public string? SupersedesAuthorizationId { get; }

    /// <summary>
    /// The digest of the authorisation this one replaces. Digest-covered from <c>@3</c> onward.
    /// </summary>
    public string? SupersedesAuthorizationDigest { get; }

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
    /// <para>
    /// Every failure throws rather than returning a permissive default. An authorisation that
    /// cannot be read is not an authorisation for everything, and this is the one place in the
    /// acquisition path where that mistake would be unrecoverable.
    /// </para>
    /// <para>
    /// <paramref name="declarationRoot"/> is the directory a <c>@3</c> authorisation's
    /// <c>DeclarationPath</c> is resolved against. It defaults to the parent of the directory
    /// holding the authorisation, which is the repository root for anything in
    /// <c>declarations/</c>. A test writing a fixture elsewhere passes its own root rather than
    /// having the loader guess.
    /// </para>
    /// </remarks>
    public static AcquisitionAuthorization Load(
        string path,
        string expectedEvidenceBase,
        string? declarationRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEvidenceBase);

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var root = document.RootElement;
        var schema = Text(root, "Schema");
        var superseding = string.Equals(schema, SupersedingSchema, StringComparison.Ordinal);
        var documentScoped = string.Equals(schema, DocumentScopedSchema, StringComparison.Ordinal);

        if (!superseding && !documentScoped && !string.Equals(schema, Schema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The authorisation declares schema '{schema}', which this loader does not " +
                $"understand. Expected '{Schema}', '{SupersedingSchema}' or " +
                $"'{DocumentScopedSchema}'. Refused rather than interpreted.");
        }

        // A schema may carry only the fields its own canonical form protects. The rule is not new:
        // @1 has always refused the @2 fields for this reason, and @3's fields are exactly the ones
        // that would have repeated the mistake had they been bolted onto @1.
        if (!documentScoped)
        {
            foreach (var property in ProtectedDocumentScopedProperties)
            {
                if (root.TryGetProperty(property, out _))
                {
                    throw new InvalidOperationException(
                        $"A '{schema}' authorisation carries {property}, and that schema's digest " +
                        "does not cover it. A field the digest cannot protect must not be present, " +
                        "because it would read as evidence while being editable. Declare " +
                        $"'{DocumentScopedSchema}' instead. Refused.");
                }
            }
        }

        var carriesSupersession =
            root.TryGetProperty(SupersedesProperty, out _) ||
            root.TryGetProperty(AlreadyConsumedProperty, out _);

        if (!superseding && !documentScoped && carriesSupersession)
        {
            throw new InvalidOperationException(
                $"A '{Schema}' authorisation carries {SupersedesProperty} or " +
                $"{AlreadyConsumedProperty}, and that schema's digest does not cover them. A field " +
                "the digest cannot protect must not be present, because it would read as evidence " +
                $"while being editable. Declare '{SupersedingSchema}' instead. Refused.");
        }

        string? supersedes = null;
        var alreadyConsumed = 0;
        string? supersedesDigest = null;

        if (superseding || (documentScoped && carriesSupersession))
        {
            supersedes = Text(root, SupersedesProperty);
            alreadyConsumed = root.GetProperty(AlreadyConsumedProperty).GetInt32();

            if (string.IsNullOrWhiteSpace(supersedes))
            {
                throw new InvalidOperationException(
                    $"A '{schema}' authorisation must name the authorisation it " +
                    "replaces. One that supersedes nothing is an original, not a successor.");
            }

            if (alreadyConsumed < 0)
            {
                throw new InvalidOperationException(
                    $"{AlreadyConsumedProperty} is {alreadyConsumed}. Spending cannot be negative, " +
                    "and an authorisation that claimed it was is describing a credit-back that this " +
                    "platform does not have.");
            }

            // Only @3 protects it, so only @3 may carry it.
            if (documentScoped)
            {
                supersedesDigest = root.TryGetProperty(SupersedesDigestProperty, out var d)
                    ? d.GetString()
                    : null;
            }
            else if (root.TryGetProperty(SupersedesDigestProperty, out _))
            {
                // Present in the @2 files already on disk and outside their canonical form. Not
                // made an error here, because doing so would refuse three declarations that were
                // approved years of work ago; @3 fixes it going forward instead.
                supersedesDigest = null;
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

        DocumentScope? scope = null;

        if (documentScoped)
        {
            scope = ReadDocumentScopeFields(root);
        }

        var computed = ComputeDigest(
            schema, evidenceBase, vendor, sources, from, to, planned, satisfied, ceiling, symbols,
            supersedes, alreadyConsumed, supersedesDigest, scope);

        if (!string.Equals(computed, stated, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The authorisation's digest does not match its content. Something changed after it " +
                $"was approved. Stated `{stated}`, computed `{computed}`. Refused.");
        }

        // Only now is the declaration read. The order is the security property: until the digest
        // verifies, every field in this file is attacker-supplied text, and DeclarationPath is a
        // filesystem path. Resolving it first would mean opening a path nobody had authenticated.
        if (scope is not null)
        {
            VerifyDeclarationBinding(scope, path, declarationRoot);
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
            alreadyConsumed,
            supersedesDigest,
            scope);
    }

    /// <summary>
    /// Reads a <c>@3</c> declaration binding, and verifies it against the declaration on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every stated value is recomputed, not trusted.</strong> The authorisation's own
    /// digest already makes these fields unforgeable as <em>text</em>; recomputing them from the
    /// file makes them a binding to an actual document. Without this, an authorisation could be
    /// internally perfect and name a declaration that had since changed.
    /// </para>
    /// <para>
    /// Both hashes are checked, because they fail differently. The whole-file SHA-256 catches any
    /// byte change at all, including prose. The declaration's own digest catches a change to the
    /// declared scope specifically, because a pilot declaration's digest rule deliberately excludes
    /// prose - so clarifying a note does not invalidate an authorisation while changing a target
    /// does.
    /// </para>
    /// </remarks>
    private static DocumentScope ReadDocumentScopeFields(JsonElement root)
    {
        foreach (var property in DocumentScopedProperties)
        {
            if (!root.TryGetProperty(property, out _))
            {
                throw new InvalidOperationException(
                    $"A '{DocumentScopedSchema}' authorisation must carry {property}. Every one of " +
                    "these fields is in the canonical form, so an absent one is not a default - it " +
                    "is a digest that cannot be computed. Refused.");
            }
        }

        var dataCategory = Text(root, "DataCategory");
        var subjectKind = Text(root, "SubjectKind");
        var declarationPath = Text(root, "DeclarationPath");
        var declarationSchema = Text(root, "DeclarationSchema");
        var digestField = Text(root, "DeclarationDigestField");
        var declarationDigest = Text(root, "DeclarationDigest");
        var declarationSha = Text(root, "DeclarationSha256");
        var issued = Instant(root, "IssuedAtUtc");
        var expires = Instant(root, "ExpiresAtUtc");

        if (expires <= issued)
        {
            throw new InvalidOperationException(
                $"ExpiresAtUtc ({Format(expires)}) is not after IssuedAtUtc ({Format(issued)}). An " +
                "authorisation that expires before it begins cannot authorise anything, and one " +
                "that expires exactly as it begins is a permission nobody can use.");
        }

        if (expires - issued > TimeSpan.FromDays(MaximumLifetimeDays))
        {
            throw new InvalidOperationException(
                $"The authorisation is valid for {(expires - issued).TotalDays:0.##} days, above " +
                $"the maximum of {MaximumLifetimeDays}. A long-lived authorisation is a permanent " +
                "one with extra steps, and the bound is enforced here rather than left to whoever " +
                "writes the file.");
        }

        return new DocumentScope(
            dataCategory,
            subjectKind,
            declarationPath,
            declarationSchema,
            digestField,
            declarationDigest.ToLowerInvariant(),
            declarationSha.ToLowerInvariant(),
            issued,
            expires);
    }

    /// <summary>
    /// Verifies a digest-verified binding against the declaration actually on disk.
    /// </summary>
    /// <remarks>
    /// Called only after the authorisation's own digest has verified, so everything here is a value
    /// that was signed rather than a value that was merely written.
    /// </remarks>
    private static void VerifyDeclarationBinding(
        DocumentScope scope,
        string authorizationPath,
        string? declarationRoot)
    {
        var declarationPath = scope.DeclarationPath;
        var declarationSha = scope.DeclarationSha256;
        var declarationSchema = scope.DeclarationSchema;
        var digestField = scope.DeclarationDigestField;
        var declarationDigest = scope.DeclarationDigest;

        var resolvedRoot = declarationRoot
            ?? Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(authorizationPath)))
            ?? throw new InvalidOperationException(
                $"The authorisation at '{authorizationPath}' has no resolvable parent directory, so " +
                "the declaration it names cannot be found. Refused rather than searched for.");

        if (Path.IsPathRooted(declarationPath))
        {
            throw new InvalidOperationException(
                $"The declaration path '{declarationPath}' is absolute. A binding is stated relative " +
                "to the repository so that it means the same thing on every machine, and an " +
                "absolute path means whatever that machine happens to hold.");
        }

        var fullRoot = Path.GetFullPath(resolvedRoot);
        var resolved = Path.GetFullPath(Path.Combine(fullRoot, declarationPath));

        // A signed path is still a path. Keeping it inside the root costs nothing and means a
        // declaration reference can never name a file outside the repository it is a reference into.
        if (!resolved.StartsWith(
                fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The declaration path '{declarationPath}' resolves outside the repository root. A " +
                "binding that can escape the root is not a binding to this repository's evidence.");
        }

        if (!File.Exists(resolved))
        {
            throw new InvalidOperationException(
                $"The authorisation names declaration '{declarationPath}', which does not exist " +
                "under the expected root. An authorisation whose declaration cannot be read is not " +
                "an authorisation for the targets it happens to list.");
        }

        var bytes = File.ReadAllBytes(resolved);
        var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        if (!string.Equals(actualSha, declarationSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The declaration at '{declarationPath}' hashes to `{actualSha}` and the " +
                $"authorisation was signed against `{declarationSha}`. The declaration changed " +
                "after the authorisation was approved. Refused.");
        }

        using var declaration = JsonDocument.Parse(bytes);

        var declarationRootElement = declaration.RootElement;
        var actualSchema = Text(declarationRootElement, "Schema");

        if (!string.Equals(actualSchema, declarationSchema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The declaration at '{declarationPath}' declares schema '{actualSchema}' and the " +
                $"authorisation was signed against '{declarationSchema}'. A declaration of another " +
                "kind is a different document, whatever else about it matches.");
        }

        if (!declarationRootElement.TryGetProperty(digestField, out var digestElement))
        {
            throw new InvalidOperationException(
                $"The declaration at '{declarationPath}' carries no field '{digestField}', which " +
                "the authorisation names as the digest it is bound to. Declaration schemas name " +
                "their digest differently, which is why the field is recorded rather than assumed.");
        }

        var actualDigest = digestElement.GetString() ?? string.Empty;

        if (!string.Equals(actualDigest, declarationDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The declaration's {digestField} is `{actualDigest}` and the authorisation was " +
                $"signed against `{declarationDigest}`. The declared scope changed after the " +
                "authorisation was approved. Refused.");
        }
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
            supersedesAuthorizationId, alreadyConsumed, null, null);

    /// <summary>
    /// The digest of a <c>@3</c> declaration's content, for writing one that will load.
    /// </summary>
    /// <remarks>
    /// A separate method rather than optional parameters on <see cref="DigestFor"/>, so that every
    /// existing <c>@1</c> and <c>@2</c> call site is byte-for-byte the call it always was and
    /// cannot acquire a <c>@3</c> tail by accident.
    /// </remarks>
    public static string DigestForDocumentScoped(
        string evidenceBase,
        string vendor,
        IReadOnlyList<string> sources,
        DateOnly from,
        DateOnly to,
        int planned,
        int satisfied,
        int ceiling,
        IReadOnlyList<string> symbols,
        DocumentScope scope,
        string? supersedesAuthorizationId = null,
        int alreadyConsumed = 0,
        string? supersedesAuthorizationDigest = null) =>
        ComputeDigest(
            DocumentScopedSchema, evidenceBase, vendor, sources, from, to, planned, satisfied,
            ceiling, symbols, supersedesAuthorizationId, alreadyConsumed,
            supersedesAuthorizationDigest, scope);

    /// <summary>
    /// Whether this authorisation may cause an external request at <paramref name="nowUtc"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Exclusive at the far end.</strong> Valid while <c>IssuedAtUtc &lt;= now &lt;
    /// ExpiresAtUtc</c>. An inclusive bound makes the boundary instant answer differently to two
    /// clocks one tick apart; exclusive gives one answer, and expired is the safe side of an
    /// ambiguity.
    /// </para>
    /// <para>
    /// <strong>The caller supplies the instant, and must take it from <c>IClock</c>.</strong>
    /// Reading the machine clock here would make every expiry test wait for real time, and would
    /// put a second source of "now" beside the one the rest of the acquisition path uses.
    /// </para>
    /// <para>
    /// An authorisation with no expiry - <c>@1</c> and <c>@2</c> - is valid whenever it is asked,
    /// because it has no validity span to be outside of. That is a statement about those schemas
    /// rather than a permissive default: they are bounded by their ceiling and their batch
    /// approval, and pretending otherwise would invent a bound they never had.
    /// </para>
    /// </remarks>
    public Verdict IsValidAt(DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "The instant an authorisation is judged at must be UTC. A local instant would make " +
                "the validity of an authorisation depend on where the process is running.",
                nameof(nowUtc));
        }

        if (Scope is not { } scope)
        {
            return new Verdict(true, null);
        }

        if (nowUtc < scope.IssuedAtUtc)
        {
            return new Verdict(
                false,
                Inv($"[{NotYetValidRule}] the authorisation is valid from {Format(scope.IssuedAtUtc)} and it is {Format(nowUtc)}"));
        }

        if (nowUtc >= scope.ExpiresAtUtc)
        {
            return new Verdict(
                false,
                Inv($"[{ExpiredRule}] the authorisation expired at {Format(scope.ExpiresAtUtc)} and it is {Format(nowUtc)}; an expired authorisation cannot be resumed, and a successor must be sealed"));
        }

        return new Verdict(true, null);
    }

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
    /// Whether this request is covered, for a category and subject kind as well as a subject.
    /// </summary>
    /// <remarks>
    /// A <c>@3</c> authorisation is for one category and one subject kind, both inside its digest.
    /// Checking them here stops a scope of thirty documents being spent as a scope of thirty
    /// companies against another category - which the subject identifiers alone would not prevent,
    /// because a set of strings does not know what kind of thing it names.
    /// </remarks>
    public Verdict Covers(
        string source,
        string symbol,
        DateOnly from,
        DateOnly to,
        string dataCategory,
        string subjectKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataCategory);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectKind);

        if (Scope is { } scope)
        {
            if (!string.Equals(scope.DataCategory, dataCategory, StringComparison.Ordinal))
            {
                return new Verdict(false, Inv($"category `{dataCategory}` is not the authorised `{scope.DataCategory}`"));
            }

            if (!string.Equals(scope.SubjectKind, subjectKind, StringComparison.Ordinal))
            {
                return new Verdict(false, Inv($"subject kind `{subjectKind}` is not the authorised `{scope.SubjectKind}`"));
            }
        }

        return Covers(source, symbol, from, to);
    }

    /// <summary>
    /// Claims one dispatch. Returns false when the ceiling is reached or the request is out of scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this immediately before the request leaves, and only for requests that will leave. A
    /// suppressed or refused request must not be passed here - see <see cref="Suppressed"/>, which
    /// records that it happened and consumes nothing.
    /// </para>
    /// <para>
    /// <strong>A <c>@3</c> authorisation cannot be consumed through this overload.</strong> It has
    /// a validity span, and an overload with no instant has no way to check it - so rather than
    /// silently treating an expiring authorisation as a standing one, this refuses to answer. A
    /// caller holding a <c>@3</c> authorisation must use the overload that takes the clock's
    /// instant.
    /// </para>
    /// </remarks>
    public Verdict TryConsume(string source, string symbol, DateOnly from, DateOnly to)
    {
        if (IsDocumentScoped)
        {
            throw new InvalidOperationException(
                $"Authorisation '{AuthorizationId}' is '{DocumentScopedSchema}' and has a validity " +
                "span, so it cannot be consumed without the instant to judge it at. Use the " +
                "overload that takes nowUtc, and take that instant from IClock. Treating an " +
                "expiring authorisation as a standing one is the failure this refuses to allow.");
        }

        return ConsumeCore(source, symbol, from, to);
    }

    /// <summary>
    /// Claims one dispatch at a stated instant, checking validity before scope and ceiling.
    /// </summary>
    /// <remarks>
    /// Validity is judged first because an expired authorisation is not "out of budget" or "out of
    /// scope" - it is not an authorisation at all at that instant, and reporting it as either of
    /// the others would send whoever reads the refusal looking in the wrong place.
    /// </remarks>
    public Verdict TryConsume(
        string source,
        string symbol,
        DateOnly from,
        DateOnly to,
        DateTime nowUtc,
        string? dataCategory = null,
        string? subjectKind = null)
    {
        var valid = IsValidAt(nowUtc);

        if (!valid.Allowed)
        {
            return valid;
        }

        if (dataCategory is not null && subjectKind is not null)
        {
            var scoped = Covers(source, symbol, from, to, dataCategory, subjectKind);

            if (!scoped.Allowed)
            {
                return scoped;
            }
        }

        return ConsumeCore(source, symbol, from, to);
    }

    private Verdict ConsumeCore(string source, string symbol, DateOnly from, DateOnly to)
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
        int alreadyConsumed,
        string? supersedesAuthorizationDigest,
        DocumentScope? scope)
    {
        var canonical = new StringBuilder();

        // The schema itself leads the canonical form, so a '@1' file, a '@2' file and a '@3' file
        // can never hash alike and the '@1' form is byte-for-byte what it always was.
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

        // The @3 tail, appended after the symbols and before the supersession tail. Emitted only
        // for @3, so nothing about the @1 or @2 canonical form moves.
        if (scope is not null)
        {
            canonical.Append('\n').Append(scope.DataCategory);
            canonical.Append('\n').Append(scope.SubjectKind);
            canonical.Append('\n').Append(scope.DeclarationPath);
            canonical.Append('\n').Append(scope.DeclarationSchema);
            canonical.Append('\n').Append(scope.DeclarationDigestField);
            canonical.Append('\n').Append(scope.DeclarationDigest);
            canonical.Append('\n').Append(scope.DeclarationSha256);
            canonical.Append('\n').Append(Format(scope.IssuedAtUtc));
            canonical.Append('\n').Append(Format(scope.ExpiresAtUtc));
        }

        // Appended last and only when present, so nothing about the '@1' canonical form moves.
        if (supersedesAuthorizationId is not null)
        {
            canonical.Append('\n').Append(supersedesAuthorizationId);
            canonical.Append('\n').Append(alreadyConsumed.ToString(CultureInfo.InvariantCulture));

            // Protected from @3 onward. A @2 file carries it on disk and outside its canonical
            // form, which is exactly the defect @3 exists not to repeat.
            if (supersedesAuthorizationDigest is not null)
            {
                canonical.Append('\n').Append(supersedesAuthorizationDigest);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static string Text(JsonElement root, string property) =>
        root.GetProperty(property).GetString() ?? string.Empty;

    private static DateOnly Date(JsonElement root, string property) =>
        DateOnly.ParseExact(Text(root, property), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a <c>@3</c> instant, in UTC or not at all.
    /// </summary>
    /// <remarks>
    /// Parsed with a fixed format and <see cref="DateTimeStyles.AdjustToUniversal"/> rather than
    /// converted from whatever was written. A local instant silently adjusted into UTC would make
    /// an authorisation expire at a different moment depending on where the file was produced,
    /// which is the class of bug the five-instant rule exists to prevent.
    /// </remarks>
    private static DateTime Instant(JsonElement root, string property)
    {
        var text = Text(root, property);

        if (!DateTime.TryParseExact(
                text,
                InstantFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            throw new InvalidOperationException(
                $"{property} is `{text}`, which is not an instant of the form {InstantFormat}. It " +
                "is refused rather than coerced: an authorisation whose validity depends on how a " +
                "string was guessed at is not a bound.");
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private static string Format(DateTime instant) =>
        instant.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture);

    private static string Inv(FormattableString text) =>
        text.ToString(CultureInfo.InvariantCulture);

    /// <param name="Allowed">Whether this request may leave the platform.</param>
    /// <param name="Reason">Why not, when not. Null exactly when allowed.</param>
    internal sealed record Verdict(bool Allowed, string? Reason);

    /// <summary>
    /// What a <c>@3</c> authorisation is for, and between which instants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every field here is in the canonical form, so none of them can be edited after signing
    /// without invalidating the authorisation - and the three declaration fields are additionally
    /// recomputed from the declaration on disk at load, so none of them can be true of a file that
    /// has since changed.
    /// </para>
    /// <para>
    /// <strong><see cref="IssuedAtUtc"/> is also the validity start.</strong> There is deliberately
    /// no separate not-before instant: a future-dated start would add a third state - sealed but not
    /// yet live - with no use case here and one more way to be wrong.
    /// </para>
    /// <para>
    /// <strong>None of this is the acquisition window.</strong> <c>WindowFromUtc</c> and
    /// <c>WindowToUtc</c> are an evidence scope compared in <see cref="Covers"/>, and the targets'
    /// own filing dates are historical facts about documents. Three different meanings, and
    /// collapsing any two of them would be the mistake the five-instant rule exists to prevent.
    /// </para>
    /// </remarks>
    /// <param name="DataCategory">The one category this authorisation is for.</param>
    /// <param name="SubjectKind">The one subject kind its symbols are.</param>
    /// <param name="DeclarationPath">Repository-relative path of the sealed declaration.</param>
    /// <param name="DeclarationSchema">The schema that declaration must declare.</param>
    /// <param name="DeclarationDigestField">Which field of it carries the digest bound below.</param>
    /// <param name="DeclarationDigest">That field's value, recomputed from the file at load.</param>
    /// <param name="DeclarationSha256">The declaration's whole-file hash, recomputed at load.</param>
    /// <param name="IssuedAtUtc">When it was sealed, and the instant its validity begins.</param>
    /// <param name="ExpiresAtUtc">The exclusive instant its validity ends.</param>
    internal sealed record DocumentScope(
        string DataCategory,
        string SubjectKind,
        string DeclarationPath,
        string DeclarationSchema,
        string DeclarationDigestField,
        string DeclarationDigest,
        string DeclarationSha256,
        DateTime IssuedAtUtc,
        DateTime ExpiresAtUtc);
}
