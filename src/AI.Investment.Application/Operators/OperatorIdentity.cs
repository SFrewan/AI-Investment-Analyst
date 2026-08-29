using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Application.Operators;

/// <summary>
/// One thing an operator is permitted to do.
/// </summary>
/// <remarks>
/// <para>
/// A set of named privileges rather than a role, and not a <c>[Flags]</c> enum. An operator holds
/// a set, and the set is modelled as a set - the same choice <c>DataCategory</c> makes and for the
/// same reason: roles bundle privileges that were never argued for together, and flags run out.
/// </para>
/// <para>
/// <see cref="None"/> is zero so that a default-initialised value permits nothing. Every check in
/// this system reads a privilege before it acts; a default that happened to mean "may administer
/// the kill switch" is exactly the accident this ordering makes impossible.
/// </para>
/// <para>
/// Read privileges are deliberately absent. The read endpoints are unauthenticated today, and
/// inventing privileges that nothing enforces would make this list a description of intentions
/// rather than of behaviour.
/// </para>
/// </remarks>
public enum OperatorPrivilege
{
    /// <summary>Permits nothing. The value of a claim nobody granted.</summary>
    None = 0,

    /// <summary>Reject an opportunity the platform put forward.</summary>
    DecideOpportunities = 1,

    /// <summary>Acknowledge and resolve escalations.</summary>
    AnswerEscalations = 2,

    /// <summary>Engage the kill switch.</summary>
    AdministerKillSwitch = 3,

    /// <summary>Create watches and activate registered sources.</summary>
    AdministerWatches = 4,

    /// <summary>
    /// May read the portfolio: holdings, cost, realised and unrealised profit, exposure.
    /// </summary>
    /// <remarks>
    /// Separate from the four decision privileges because it is the only read privilege, and
    /// because financial state is the thing most often wanted by somebody who should not be able to
    /// act on it. Granting it confers no ability to change anything.
    /// </remarks>
    ViewPortfolio = 5,
}

/// <summary>
/// Who is asking, and what they are allowed to ask for.
/// </summary>
/// <remarks>
/// <para>
/// The application layer's view of an authenticated person. It carries no credential, no token and
/// no transport detail: the API turns whatever scheme it uses into one of these, and everything
/// below the controller sees only an identity and a set of privileges. That is what lets the
/// authentication mechanism change without any of the decisions moving.
/// </para>
/// <para>
/// <see cref="Id"/> is what reaches the audit trail. It is bounded at the same length as
/// <c>AuditRecord.Actor</c> and <c>ProposedBy.Id</c> on purpose - an identity that could be recorded
/// on a proposal but not on the audit record of that proposal would be an identity that vanishes at
/// exactly the point somebody wants it.
/// </para>
/// </remarks>
public sealed record OperatorIdentity
{
    public const int MaxIdLength = 120;
    public const int MaxDisplayNameLength = 120;

    private readonly HashSet<OperatorPrivilege> _privileges;

    private OperatorIdentity(string id, string displayName, HashSet<OperatorPrivilege> privileges)
    {
        Id = id;
        DisplayName = displayName;
        _privileges = privileges;
    }

    /// <summary>The identifier written into every proposal and audit record this operator causes.</summary>
    public string Id { get; }

    /// <summary>What a person is called on screen. Never used for a decision.</summary>
    public string DisplayName { get; }

    public IReadOnlyCollection<OperatorPrivilege> Privileges => _privileges;

    /// <summary>Whether this operator holds one privilege. False for anything not granted.</summary>
    public bool Has(OperatorPrivilege privilege) => _privileges.Contains(privilege);

    public static OperatorIdentity Create(
        string id,
        string displayName,
        IEnumerable<OperatorPrivilege> privileges)
    {
        ArgumentNullException.ThrowIfNull(privileges);

        var trimmedId = Text(id, nameof(id), MaxIdLength,
            "An operator must be identifiable. An action recorded against nobody is an action " +
            "nobody can be asked about.");

        var trimmedName = Text(displayName, nameof(displayName), MaxDisplayNameLength,
            "An operator must have a display name, so a person reading the screen knows whose " +
            "session they are looking at.");

        var granted = new HashSet<OperatorPrivilege>();

        foreach (var privilege in privileges)
        {
            if (!Enum.IsDefined(privilege) || privilege == OperatorPrivilege.None)
            {
                throw new DomainValidationException(
                    nameof(privileges),
                    $"'{privilege}' is not a privilege that can be granted. An unrecognised " +
                    "privilege is refused rather than ignored: silently dropping it would grant " +
                    "less than the configuration says and nobody would find out until it mattered.");
            }

            granted.Add(privilege);
        }

        // An operator with no privileges is permitted. It is how a read-only account is expressed,
        // and refusing it here would push installations towards granting something rather than
        // nothing.
        return new OperatorIdentity(trimmedId, trimmedName, granted);
    }

    public override string ToString() => $"{DisplayName} <{Id}> [{_privileges.Count} privileges]";

    private static string Text(string value, string parameterName, int maxLength, string reason)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(parameterName, reason);
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException(
                parameterName,
                $"A value may not exceed {maxLength} characters.");
        }

        return trimmed;
    }
}
