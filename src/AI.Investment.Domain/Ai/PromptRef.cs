using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Ai;

/// <summary>
/// A prompt, identified by owning agent, name and version -
/// <c>financial-analyst/statement-interpretation@v1.0</c>.
/// </summary>
/// <remarks>
/// <para>
/// A prompt change is a code change. It alters what the model is asked, and therefore what it
/// answers, which means an unversioned prompt silently invalidates every historical comparison the
/// platform has ever stored: two analyses recorded a month apart become incomparable for a reason
/// nobody can see in the data. Versioning is what keeps "the model got worse" and "we changed the
/// question" distinguishable.
/// </para>
/// <para>
/// The shape follows the convention <c>prompts/README.md</c> established in Phase 0, unchanged:
/// an agent folder, a prompt name, and a two-part version where <em>major</em> moves when the
/// output contract or the task changes and <em>minor</em> when the wording does. Phase 4 is the
/// first phase with a prompt in it, and adopting the convention as written was cheaper and more
/// honest than rewriting it around the first implementation that arrived.
/// </para>
/// </remarks>
public sealed record PromptRef
{
    public const int MaxSegmentLength = 64;

    private PromptRef(string agent, string name, int major, int minor)
    {
        Agent = agent;
        Name = name;
        Major = major;
        Minor = minor;
    }

    /// <summary>The owning agent's folder, lower-case.</summary>
    public string Agent { get; }

    /// <summary>The prompt's own name within that folder, lower-case.</summary>
    public string Name { get; }

    /// <summary>Moves when the output contract or the task changes.</summary>
    public int Major { get; }

    /// <summary>Moves when the wording or guidance changes and the contract does not.</summary>
    public int Minor { get; }

    /// <summary>The identity without the version: <c>financial-analyst/statement-interpretation</c>.</summary>
    public string Value => $"{Agent}/{Name}";

    /// <summary>The version as it appears in file names and audit records: <c>v1.0</c>.</summary>
    public string VersionLabel =>
        string.Create(CultureInfo.InvariantCulture, $"v{Major}.{Minor}");

    public static PromptRef Create(string agent, string name, int major, int minor)
    {
        if (major < 1)
        {
            throw new DomainValidationException(
                nameof(major),
                $"A prompt major version starts at 1. Received {major.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (minor < 0)
        {
            throw new DomainValidationException(
                nameof(minor),
                $"A prompt minor version may not be negative. " +
                $"Received {minor.ToString(CultureInfo.InvariantCulture)}.");
        }

        return new PromptRef(Slug(agent, nameof(agent)), Slug(name, nameof(name)), major, minor);
    }

    public override string ToString() => $"{Value}@{VersionLabel}";

    /// <summary>
    /// Validates one path segment of a prompt identity.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. Both segments become directory and file names, so permitting a separator,
    /// a dot or anything else that a path can be walked with would turn a prompt reference into a
    /// way to read an arbitrary file.
    /// </remarks>
    private static string Slug(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(parameterName, "A prompt identity segment is required.");
        }

        var normalised = value.Trim().ToLowerInvariant();

        if (normalised.Length > MaxSegmentLength)
        {
            throw new DomainValidationException(
                parameterName,
                $"A prompt identity segment may not exceed {MaxSegmentLength} characters. " +
                $"Received '{value}'.");
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                throw new DomainValidationException(
                    parameterName,
                    $"A prompt identity segment may contain only lower-case letters, digits and '-'. " +
                    $"Received '{value}'.");
            }
        }

        if (normalised[0] == '-' || normalised[^1] == '-')
        {
            throw new DomainValidationException(
                parameterName,
                $"A prompt identity segment may not begin or end with '-'. Received '{value}'.");
        }

        return normalised;
    }
}
