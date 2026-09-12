using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Securities;

/// <summary>
/// Answers which security an identifier denoted at an instant, from persisted assertions only.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the Security Master's stated output.</strong> Section 3.2(2) names it: "symbol
/// identity resolution as at a date". Everything it can answer comes from
/// <c>SecurityIdentifier</c> assertions that were written with an interval and a source; there is no
/// other route in, and that is the point rather than a limitation.
/// </para>
/// <para>
/// <strong>A kind and a source are part of the question, not decoration.</strong>
/// <c>VendorSymbol/eodhd/"TBCH.US"</c> and <c>Ticker/sec-edgar/"TBCH"</c> are claims by different
/// parties in different naming systems, and this never converts one into the other. A vendor key is
/// a key into a vendor's series; an exchange ticker is a claim about what traded. D5 found the
/// evidence where that distinction bites: the vendor back-fills a security's whole history under
/// the symbol it carries today, so <c>TBCH.US</c> has bars from 2021 for a security that traded as
/// <c>HEAR</c> then.
/// </para>
/// <para>
/// <strong>It reads and it never writes.</strong> No company is consulted - resolving through
/// <c>CIK -> Company -> whichever security</c> would answer a security question with a company
/// answer, and one company may have issued several. No clock is read either: the instant is an
/// argument, because a resolver that asked what time it was could not replay a past decision.
/// </para>
/// </remarks>
public sealed class SecurityResolver
{
    private readonly ISecurityRepository _securities;

    public SecurityResolver(ISecurityRepository securities)
    {
        _securities = securities ?? throw new ArgumentNullException(nameof(securities));
    }

    /// <summary>
    /// Resolves one identifier, as at one instant.
    /// </summary>
    /// <param name="kind">Which identifier system the value belongs to. Never inferred.</param>
    /// <param name="value">The identifier as the source states it. Trimmed, never normalised further.</param>
    /// <param name="source">Who made the claim. Part of the identity of the question.</param>
    /// <param name="asOfUtc">
    /// The instant to stand at. Required, and required to be UTC: a local or unspecified instant
    /// would silently shift the day the interval test is applied to.
    /// </param>
    public async Task<SecurityResolution> ResolveAsync(
        SecurityIdentifierKind kind,
        string value,
        SourceId source,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Throws rather than returning a reason. A caller that passed a local instant asked a
        // different question from the one it thinks it asked, and answering it would be worse than
        // refusing - an instant an hour either side of midnight resolves against a different day.
        //
        // Checked here rather than through the domain's own EnsureUtc, which is internal to that
        // assembly. Widening a domain helper's visibility so a read path could borrow it would be a
        // larger change than the guard it saves, and this states an argument contract rather than
        // re-implementing a rule.
        if (asOfUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                $"The as-of instant must be UTC (DateTimeKind.Utc). Received Kind={asOfUtc.Kind}.",
                nameof(asOfUtc));
        }

        if (kind == SecurityIdentifierKind.Unknown)
        {
            return SecurityResolution.Unresolved(SecurityResolutionFailure.UnsupportedIdentifierKind);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return SecurityResolution.Unresolved(SecurityResolutionFailure.NoIdentifier);
        }

        // Trimmed to match what SecurityIdentifier.Assert stored, and nothing else. No case
        // folding, no suffix stripping: "AE" and "AE.US" are different identifiers in different
        // systems, and a resolver that quietly bridged them would be the ticker-as-key failure
        // wearing a normaliser.
        var canonical = value.Trim();

        // The interval is held as dates, so the instant is reduced to the day it falls on and the
        // domain's own inclusive test is what decides. Nothing here re-implements CoversDate.
        var asOf = DateOnly.FromDateTime(asOfUtc);

        var matches = await _securities
            .FindByIdentifierAsAtAsync(kind, canonical, source, asOf, cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 1)
        {
            return SecurityResolution.Resolved(matches[0]);
        }

        if (matches.Count > 1)
        {
            return SecurityResolution.Ambiguous(matches);
        }

        return await ExplainAsync(kind, canonical, source, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Works out which kind of nothing was found, once nothing was found.
    /// </summary>
    /// <remarks>
    /// Only reached on a miss, so the extra queries cost nothing on the path that succeeds. The
    /// distinctions matter to a caller: a structural absence of dated evidence is a fact about the
    /// platform, a wrong source is a fact about who was asked, and an interval that does not reach
    /// the instant is a fact about history.
    /// </remarks>
    private async Task<SecurityResolution> ExplainAsync(
        SecurityIdentifierKind kind,
        string canonical,
        SourceId source,
        CancellationToken cancellationToken)
    {
        var (ofKind, ofKindAndValue) = await _securities
            .CountIdentifierAssertionsAsync(kind, canonical, cancellationToken)
            .ConfigureAwait(false);

        if (ofKind == 0)
        {
            // No dated assertion of this identifier system is held at all. For Ticker this is the
            // standing state D6 records - historical ticker validity evidenced for 0 of 400 - and
            // the honest answer is that the question cannot be answered from evidence, not that the
            // symbol is unknown.
            return SecurityResolution.Unresolved(SecurityResolutionFailure.NoTemporalIdentifierEvidence);
        }

        if (ofKindAndValue == 0)
        {
            return SecurityResolution.Unresolved(SecurityResolutionFailure.IdentifierNotPersisted);
        }

        var assertedByThisSource = await _securities
            .AnyIdentifierFromSourceAsync(kind, canonical, source, cancellationToken)
            .ConfigureAwait(false);

        // The value is asserted by somebody. Either this source asserted it and the interval does
        // not reach the instant, or this source never asserted it and another party did.
        return SecurityResolution.Unresolved(
            assertedByThisSource
                ? SecurityResolutionFailure.OutsideIdentifierValidity
                : SecurityResolutionFailure.NoMatchingSource);
    }
}
