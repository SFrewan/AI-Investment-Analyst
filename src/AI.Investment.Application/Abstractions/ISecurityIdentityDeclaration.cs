using AI.Investment.Application.Securities;

namespace AI.Investment.Application.Abstractions;

/// <summary>
/// The sealed identity evidence, already verified against the digest it carries.
/// </summary>
/// <remarks>
/// <para>
/// An abstraction so that the decision about what may be created is taken in the Application layer
/// while the file format stays in Infrastructure - the arrangement <c>StrategyRegister</c> already
/// uses for the strategy register. It is not a file reader: an implementation is handed content and
/// refuses it on a digest mismatch, so a declaration quietly edited stops the run rather than
/// changing what gets created.
/// </para>
/// <para>
/// There is deliberately no method that fetches, refreshes or reconciles. The declaration is
/// evidence that was sealed before this stage existed, and a population run reads it and nothing
/// else - no provider, no network, no database of record.
/// </para>
/// </remarks>
public interface ISecurityIdentityDeclaration
{
    /// <summary>Which declaration this is.</summary>
    string DeclarationId { get; }

    /// <summary>The evidence-base fingerprint the declaration is scoped to.</summary>
    string EvidenceBaseFingerprint { get; }

    /// <summary>The digest the declaration carried, recomputed and matched when it was read.</summary>
    string IdentityDigest { get; }

    /// <summary>Every member, in the order the declaration states them. Nothing is filtered.</summary>
    IReadOnlyList<SecurityIdentityEvidence> Members { get; }
}
