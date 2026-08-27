using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Analytics;

/// <summary>
/// Which revision of a formula produced a result.
/// </summary>
/// <remarks>
/// <para>
/// Stored measurements outlive the code that produced them. Without a version, a number computed
/// last year and a number computed today are indistinguishable even when the formula between them
/// changed - and a backtest comparing the two would be measuring the change in the code rather
/// than the change in the world.
/// </para>
/// <para>
/// Two parts, by convention: raise <see cref="Major"/> when the result of the same inputs would
/// change, raise <see cref="Minor"/> for anything else. That is the distinction a later
/// recalculation needs in order to decide whether stored history must be discarded.
/// </para>
/// <para>
/// No <c>IComparable</c>, and no comparison operators. Versions are compared for "is this the
/// newer formula", which <see cref="IsNewerThan"/> says plainly; ordering them like numbers would
/// invite arithmetic that means nothing.
/// </para>
/// </remarks>
public sealed record CalculationVersion
{
    public const int MinimumMajor = 1;

    private CalculationVersion(int major, int minor)
    {
        Major = major;
        Minor = minor;
    }

    public int Major { get; }

    public int Minor { get; }

    public static CalculationVersion Create(int major, int minor)
    {
        if (major < MinimumMajor)
        {
            throw new DomainValidationException(
                nameof(major),
                $"A calculation version starts at {MinimumMajor}. A version of {major} reads as " +
                "'not versioned', which is the state this type exists to make impossible.");
        }

        if (minor < 0)
        {
            throw new DomainValidationException(
                nameof(minor),
                $"A calculation version's minor part may not be negative. Received {minor}.");
        }

        return new CalculationVersion(major, minor);
    }

    /// <summary>Reads a version back from its stored form, with or without the leading 'v'.</summary>
    public static CalculationVersion Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(nameof(value), "A calculation version is required.");
        }

        var trimmed = value.Trim();

        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var parts = trimmed.Split('.');

        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            throw new DomainValidationException(
                nameof(value),
                $"A calculation version is written 'v1.0'. Received '{value}'.");
        }

        return Create(major, minor);
    }

    /// <summary>Whether this version supersedes <paramref name="other"/>.</summary>
    public bool IsNewerThan(CalculationVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Major != other.Major ? Major > other.Major : Minor > other.Minor;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"v{Major}.{Minor}");
}
