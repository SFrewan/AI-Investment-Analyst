using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Api.Security;

/// <summary>
/// The operators this installation recognises, and what each may do.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keys are never stored.</strong> Configuration holds the SHA-256 of each key, in
/// lower-case hexadecimal, and the handler hashes what arrives and compares digests. An operator
/// list is deployment configuration and ends up in a repository, a container image or a
/// configuration server sooner or later; storing the key itself would put a credential in every one
/// of them.
/// </para>
/// <para>
/// <strong>Empty means nobody.</strong> An installation that has configured no operators
/// authenticates no one, and every operator endpoint answers 401. That is the fail-closed default
/// and it is why this section is shipped empty rather than with an example account in it.
/// </para>
/// <para>
/// This is a bearer credential, not an identity provider. It is the smallest thing that gives every
/// sensitive action a name, and it is deliberately shaped so that replacing it with OIDC changes
/// this file and the handler beside it and nothing below the controller - the application layer sees
/// an <c>OperatorIdentity</c> either way.
/// </para>
/// </remarks>
public sealed class OperatorOptions
{
    public const string SectionName = "Operators";

    /// <summary>The recognised accounts. Empty by default, and empty means nobody.</summary>
    public IReadOnlyList<OperatorAccountOptions> Accounts { get; init; } = [];
}

/// <summary>One recognised operator.</summary>
public sealed class OperatorAccountOptions
{
    /// <summary>The identifier written into every proposal and audit record this operator causes.</summary>
    [Required]
    [MaxLength(120)]
    public string Id { get; init; } = string.Empty;

    /// <summary>What they are called on screen.</summary>
    [Required]
    [MaxLength(120)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// SHA-256 of the operator's key, lower-case hexadecimal, 64 characters.
    /// </summary>
    /// <remarks>
    /// Generate with <c>echo -n "the-key" | sha256sum</c>. A malformed value authenticates nobody:
    /// the handler refuses an entry it cannot parse rather than treating it as a wildcard.
    /// </remarks>
    [Required]
    [RegularExpression("^[0-9a-f]{64}$")]
    public string KeySha256 { get; init; } = string.Empty;

    /// <summary>
    /// The privileges granted, by name: DecideOpportunities, AnswerEscalations,
    /// AdministerKillSwitch, AdministerWatches.
    /// </summary>
    /// <remarks>
    /// An unrecognised name is refused rather than ignored, because silently dropping it would grant
    /// less than the configuration says and nobody would find out until it mattered.
    /// </remarks>
    public IReadOnlyList<string> Privileges { get; init; } = [];
}
