using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Securities;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Infrastructure.Identity;

/// <summary>
/// The sealed issuer-stated identity evidence, parsed from supplied content and refused on a
/// digest mismatch.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It parses content; it does not read the filesystem.</strong> That is the shape
/// <c>StrategyRegister</c> established and the reason is the same: the caller owns file access, this
/// owns verification, and a test can exercise every branch without a repository layout. A generic
/// file reader that walked up to find a declaration would make the evidence reachable from anywhere
/// and verifiable nowhere.
/// </para>
/// <para>
/// <strong>The digest check is the point.</strong> The declaration states its own canonical form -
/// schema, evidence base, declared-at, then each member's identity fields in CIK order - and this
/// recomputes it and refuses the whole file when it differs. So a symbol quietly changed, a refusal
/// softened into an acceptance, or a member's class promoted stops the run rather than changing
/// which securities get created.
/// </para>
/// <para>
/// <strong>What it does not do.</strong> It resolves nothing, fetches nothing and normalises
/// nothing. A member the declaration marks ambiguous arrives here ambiguous and leaves ambiguous;
/// the decision about what that means belongs to the policy, in the Application layer, where it can
/// be read without knowing anything about JSON.
/// </para>
/// </remarks>
public sealed class SecurityIdentityDeclaration : ISecurityIdentityDeclaration
{
    /// <summary>
    /// The active sealed declaration, relative to the repository root.
    /// </summary>
    /// <remarks>
    /// Points at the F1a superseding declaration. Its predecessor is not deleted and stays readable
    /// through <see cref="PredecessorRelativePath"/>: a superseded declaration is still the evidence
    /// that produced whatever was populated from it, and a chain that cannot be walked backwards is
    /// not auditable.
    /// </remarks>
    public const string RelativePath = "declarations/security-identity-sample400-2021-2026-v2.json";

    /// <summary>
    /// The declaration the active one supersedes. Still readable, never the active input.
    /// </summary>
    public const string PredecessorRelativePath =
        "declarations/security-identity-sample400-2021-2026.json";

    /// <summary>
    /// The original schema. Read exactly as before, including its own digest form.
    /// </summary>
    /// <remarks>
    /// Its canonical form covers neither the identifier's source nor its interval - which is why a
    /// wrong source could be sealed under it without breaking its own digest. It is kept readable
    /// rather than retired, because re-reading a sealed file under a later rule would change what
    /// that file is taken to have said.
    /// </remarks>
    public const string SchemaV1 = "security-identity-evidence@1";

    /// <summary>
    /// The active schema. Its canonical form additionally covers the vendor symbol's source and
    /// both interval bounds, so a change to either now breaks the seal.
    /// </summary>
    public const string SchemaV2 = "security-identity-evidence@2";

    /// <summary>The schema the active declaration is expected to declare.</summary>
    public const string ActiveSchema = SchemaV2;

    /// <summary>
    /// The declaration's member-list field.
    /// </summary>
    /// <remarks>
    /// A named constant rather than a literal because the JSON field name and this class's
    /// <see cref="Members"/> property are different things that happen to share a spelling. Tying
    /// one to the other with <c>nameof</c> would mean renaming a C# property silently changed which
    /// part of a sealed file is read.
    /// </remarks>
    private const string MembersField = "Members";

    private SecurityIdentityDeclaration(
        string schema,
        string declarationId,
        string evidenceBaseFingerprint,
        string identityDigest,
        IReadOnlyList<SecurityIdentityEvidence> members)
    {
        Schema = schema;
        DeclarationId = declarationId;
        EvidenceBaseFingerprint = evidenceBaseFingerprint;
        IdentityDigest = identityDigest;
        Members = members;
    }

    /// <summary>Which schema the content declared. Never inferred and never rewritten.</summary>
    public string Schema { get; }

    public string DeclarationId { get; }

    public string EvidenceBaseFingerprint { get; }

    public string IdentityDigest { get; }

    public IReadOnlyList<SecurityIdentityEvidence> Members { get; }

    /// <summary>Reads the declaration, refusing it when the recomputed digest does not match.</summary>
    public static SecurityIdentityDeclaration Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);

        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new DomainValidationException(
                nameof(json),
                "The identity declaration is an object. Content that is not one cannot carry the " +
                "provenance fields the evidence depends on.");
        }

        var schema = RequiredString(root, "Schema");

        if (!string.Equals(schema, SchemaV1, StringComparison.Ordinal) &&
            !string.Equals(schema, SchemaV2, StringComparison.Ordinal))
        {
            throw new DomainValidationException(
                nameof(json),
                $"This reader understands '{SchemaV1}' and '{SchemaV2}'. The content declares " +
                $"'{schema}'. A schema it has not been taught may mean different things by the same " +
                "field names, so it is refused rather than read optimistically.");
        }

        var declarationId = RequiredString(root, "DeclarationId");
        var fingerprint = RequiredString(root, "EvidenceBaseFingerprint");
        var declaredAtUtc = RequiredString(root, "DeclaredAtUtc");
        var declaredDigest = RequiredString(root, "IdentityDigest");

        if (!root.TryGetProperty(MembersField, out var membersElement) ||
            membersElement.ValueKind != JsonValueKind.Array)
        {
            throw new DomainValidationException(
                nameof(json),
                "The identity declaration carries a list of members. A declaration with no member " +
                "list states nothing and must not be read as stating that there is nothing.");
        }

        var members = new List<SecurityIdentityEvidence>(membersElement.GetArrayLength());

        foreach (var element in membersElement.EnumerateArray())
        {
            members.Add(ReadMember(element));
        }

        if (members.Count == 0)
        {
            throw new DomainValidationException(
                nameof(json),
                "The identity declaration is empty. A run that finds no evidence should say so " +
                "rather than conclude that no security exists.");
        }

        var recomputed = ComputeDigest(schema, fingerprint, declaredAtUtc, members);

        if (!string.Equals(recomputed, declaredDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainRuleViolationException(
                "SecurityIdentityDeclaration.DigestMismatch",
                $"The declaration's identity content does not match the digest it carries. " +
                $"Declared '{declaredDigest}', recomputed '{recomputed}'. The whole file is " +
                "refused: a seal that no longer describes its contents proves the opposite of what " +
                "it was written to prove.");
        }

        return new SecurityIdentityDeclaration(schema, declarationId, fingerprint, declaredDigest, members);
    }

    /// <summary>
    /// The canonical form the declaration's own <c>DigestRule</c> states, for its own schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Identity fields only. Prose is excluded deliberately, so that clarifying a note does not
    /// invalidate a seal while any change to what the evidence asserts does.
    /// </para>
    /// <para>
    /// <strong>Each schema is hashed by its own rule, and v1's is untouched.</strong> Re-hashing a
    /// v1 file under v2's wider form would not "fix" its digest - it would declare the sealed file
    /// broken, because the digest it carries was computed over six fields per member and would no
    /// longer match. A seal states what was sealed under the rule in force when it was written.
    /// </para>
    /// <para>
    /// v2 adds the vendor symbol's source and both interval bounds. v1 covered neither, which is
    /// exactly how an assertion attributed to a source that does not exist could be sealed without
    /// anything failing.
    /// </para>
    /// </remarks>
    private static string ComputeDigest(
        string schema,
        string fingerprint,
        string declaredAtUtc,
        IReadOnlyList<SecurityIdentityEvidence> members)
    {
        var includeSourceAndInterval = string.Equals(schema, SchemaV2, StringComparison.Ordinal);

        var canonical = new StringBuilder();

        canonical.Append(schema).Append('\n')
            .Append(fingerprint).Append('\n')
            .Append(declaredAtUtc);

        foreach (var member in members)
        {
            canonical.Append('\n').Append(member.Cik)
                .Append('\n').Append(member.IdentityClass)
                .Append('\n').Append(member.ResolutionStatus)
                .Append('\n').Append(member.Symbol?.Value ?? string.Empty)
                .Append('\n').Append(member.VendorSeries?.Value ?? string.Empty);

            if (includeSourceAndInterval)
            {
                canonical
                    .Append('\n').Append(member.VendorSeries?.SourceAuthority ?? string.Empty)
                    .Append('\n').Append(Iso(member.VendorSeries?.FirstBarDate))
                    .Append('\n').Append(Iso(member.VendorSeries?.LastBarDate));
            }

            canonical.Append('\n').Append(member.RejectedSymbol ?? string.Empty);
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    /// <summary>A date in the declaration's own notation, or empty when there is none.</summary>
    private static string Iso(DateOnly? date) =>
        date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static SecurityIdentityEvidence ReadMember(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new DomainValidationException(
                MembersField,
                "Every member of the identity declaration is an object.");
        }

        var symbol = ReadSymbol(element);
        var vendor = ReadVendorSeries(element);

        return new SecurityIdentityEvidence(
            RequiredString(element, "Cik"),
            OptionalString(element, "IssuerNameInEvidence"),
            RequiredString(element, "IdentityClass"),
            RequiredString(element, "ResolutionStatus"),
            RequiredString(element, "AmbiguityStatus"),
            symbol,
            vendor)
        {
            RejectedSymbol = ReadRejectedSymbol(element),
        };
    }

    private static SymbolAssertion? ReadSymbol(JsonElement member)
    {
        if (!member.TryGetProperty("SecurityIdentifierEvidence", out var evidence) ||
            evidence.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new SymbolAssertion(
            RequiredString(evidence, "Value"),
            RequiredString(evidence, "SourceAuthority"),
            RequiredBool(evidence, "IsIssuerStated"),
            RequiredBool(evidence, "IsAccepted"),
            OptionalString(evidence, "SecurityTitleObserved"),
            RequiredBool(evidence, "ValidityIntervalEvidenced"),
            OptionalDate(evidence, "ValidFrom"),
            OptionalDate(evidence, "ValidTo"));
    }

    private static VendorSeriesAssertion? ReadVendorSeries(JsonElement member)
    {
        if (!member.TryGetProperty("VendorSeriesEvidence", out var evidence) ||
            evidence.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new VendorSeriesAssertion(
            RequiredString(evidence, "Value"),
            RequiredString(evidence, "SourceAuthority"),
            RequiredDate(evidence, "SeriesFirstBarDate"),
            RequiredDate(evidence, "SeriesLastBarDate"));
    }

    private static string? ReadRejectedSymbol(JsonElement member) =>
        member.TryGetProperty("ConflictingEvidence", out var conflict) &&
        conflict.ValueKind == JsonValueKind.Object
            ? OptionalString(conflict, "RejectedValue")
            : null;

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new DomainValidationException(
                name,
                $"The identity declaration requires '{name}'. A missing field is not an empty one: " +
                "reading it as blank would turn an absent statement into a statement of absence.");
        }

        return value.GetString()!;
    }

    /// <summary>
    /// A date that may be stated as null, which is a statement rather than an omission.
    /// </summary>
    /// <remarks>
    /// A JSON null returns null, and so does an absent field - but a value that is present and not
    /// a well-formed date throws, because that is a malformed declaration rather than an absent
    /// interval.
    /// </remarks>
    private static DateOnly? OptionalDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String ||
            !DateOnly.TryParseExact(
                value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new DomainValidationException(
                name,
                $"'{name}' must be an ISO date or null. A value that is neither is a malformed " +
                "declaration, not an absent interval.");
        }

        return date;
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool RequiredBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False))
        {
            throw new DomainValidationException(
                name,
                $"The identity declaration requires '{name}' as a boolean. Defaulting it would " +
                "decide an evidence question by omission.");
        }

        return value.GetBoolean();
    }

    private static DateOnly RequiredDate(JsonElement element, string name)
    {
        var raw = RequiredString(element, name);

        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new DomainValidationException(
                name,
                $"'{name}' must be an ISO date. Received '{raw}'.");
        }

        return date;
    }
}
