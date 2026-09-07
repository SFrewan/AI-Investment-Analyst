using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>
/// What can safely be said about a configured credential without saying the credential.
/// </summary>
/// <remarks>
/// <para>
/// An operator who has just set a secret needs to answer one question - <em>did the value I set
/// reach the application?</em> - and the obvious way to answer it is to print the value, which
/// puts the secret in a console, a scrollback buffer, a screenshot and a support ticket. This
/// answers the same question from three facts that are not the value: whether anything is there,
/// how long it is, and a fingerprint that is stable across processes and machines.
/// </para>
/// <para>
/// <strong>The fingerprint is domain-separated, and that is not decoration.</strong> A plain
/// SHA-256 of a secret can be confirmed by anyone holding a guess and a hash calculator, which
/// turns a "safe" diagnostic into an oracle for a credential drawn from a small space. Prefixing a
/// constant that names this use means a digest published here cannot be checked against a rainbow
/// table or against a digest of the same secret computed anywhere else.
/// </para>
/// <para>
/// <see cref="Length"/> is deliberately exact rather than bucketed. The failure it exists to catch
/// is a value truncated by the shell that set it, and a bucket wide enough to be uninformative
/// about the secret would also be too wide to show a truncation.
/// </para>
/// </remarks>
public sealed record CredentialPresence
{
    /// <summary>The fingerprint reported when nothing is configured.</summary>
    public const string AbsentFingerprint = "absent";

    /// <summary>How many hex characters of the digest are reported.</summary>
    /// <remarks>
    /// Twelve is enough that two different credentials will not collide in practice and short
    /// enough to be read aloud, which is what a fingerprint is for.
    /// </remarks>
    public const int FingerprintCharacters = 12;

    /// <summary>
    /// The domain separator. Changing it changes every fingerprint, which is why it is a constant
    /// and not a parameter.
    /// </summary>
    private const string Domain = "AI.Investment/credential-fingerprint/v1\n";

    private CredentialPresence(bool isConfigured, bool isPadded, int length, string fingerprint)
    {
        IsConfigured = isConfigured;
        IsPadded = isPadded;
        Length = length;
        Fingerprint = fingerprint;
    }

    /// <summary>Whether a non-blank value is present.</summary>
    public bool IsConfigured { get; }

    /// <summary>Whether the value carries leading or trailing whitespace.</summary>
    /// <remarks>
    /// The single most common way a correctly copied credential still fails: a newline that came
    /// along with the paste. Reported separately because "configured, and wrong in this exact way"
    /// is a different instruction to an operator than "not configured".
    /// </remarks>
    public bool IsPadded { get; }

    /// <summary>The value's length in characters, or zero when nothing is configured.</summary>
    public int Length { get; }

    /// <summary>
    /// A stable, non-reversible identifier for the value, or <see cref="AbsentFingerprint"/>.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>Describes a credential without disclosing it.</summary>
    public static CredentialPresence Of(string? credential)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return new CredentialPresence(false, false, 0, AbsentFingerprint);
        }

        var padded = !string.Equals(credential, credential.Trim(), StringComparison.Ordinal);

        return new CredentialPresence(true, padded, credential.Length, Digest(credential));
    }

    /// <summary>
    /// A one-line report, safe to print anywhere the absence of the secret is what matters.
    /// </summary>
    public override string ToString() => IsConfigured
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"configured, {Length} characters, fingerprint {Fingerprint}{(IsPadded ? ", PADDED" : string.Empty)}")
        : "not configured";

    /// <summary>The domain-separated digest, truncated.</summary>
    private static string Digest(string credential)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Domain + credential));

        return Convert.ToHexString(bytes)[..FingerprintCharacters].ToUpperInvariant();
    }
}
