using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Approvals;

/// <summary>
/// A hash of the exact action a human was shown.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism that makes an approval an approval of <em>something</em>. Without it a token
/// authorises "an action on this opportunity", and the action that eventually executes can differ
/// in amount, target or type from the one on the screen when somebody clicked approve. With it, any
/// difference at all makes the token refuse.
/// </para>
/// <para>
/// Every field that changes what the action does is in the fingerprint - capability, type, target,
/// parameters, cost, exposure, reversibility and the computed risk tier. Nothing that merely
/// describes it is: the correlation identifier, the timestamp and the proposal's own identity are
/// excluded, so that re-presenting the same action does not produce a different hash for no
/// reason a human could see.
/// </para>
/// </remarks>
public sealed record ActionFingerprint
{
    private ActionFingerprint(string value) => Value = value;

    /// <summary>Lower-case hexadecimal SHA-256 of the canonical description.</summary>
    public string Value { get; }

    public static ActionFingerprint Of(ActionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{proposal.Capability}\n{proposal.ActionType}\n{proposal.Target}\n" +
            $"{proposal.Parameters.Describe()}\n{proposal.Economics.EstimatedCost}\n" +
            $"{proposal.Economics.EstimatedExposure}\n{proposal.Economics.Reversibility}\n" +
            $"{proposal.RiskTier}");

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return new ActionFingerprint(Convert.ToHexString(digest).ToLowerInvariant());
    }

    public static ActionFingerprint Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 64)
        {
            throw new DomainValidationException(
                nameof(value),
                "An action fingerprint is 64 hexadecimal characters. A malformed one cannot be " +
                "compared, and a comparison that cannot be made must not be treated as a match.");
        }

        var normalised = value.Trim().ToLowerInvariant();

        foreach (var c in normalised)
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                throw new DomainValidationException(
                    nameof(value),
                    $"An action fingerprint contains only hexadecimal characters. Received '{value}'.");
            }
        }

        return new ActionFingerprint(normalised);
    }

    public bool Matches(ActionProposal proposal) =>
        string.Equals(Value, Of(proposal).Value, StringComparison.Ordinal);

    public override string ToString() => Value;
}
