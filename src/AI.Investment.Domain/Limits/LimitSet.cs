using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Limits;

/// <summary>
/// The configured limits, plus the instrument allow-list.
/// </summary>
/// <remarks>
/// <para>
/// The allow-list is a field rather than a <see cref="Limit"/> because it is a set of identifiers
/// and not a scalar ceiling; <see cref="LimitKind.InstrumentAllowList"/> exists so a breach of it
/// can be reported in the same vocabulary as every other breach.
/// </para>
/// <para>
/// <strong>An empty allow-list permits everything, and that is deliberate but dangerous</strong> -
/// so it is stated here rather than left to be discovered. The alternative, an empty list permitting
/// nothing, would mean an installation that never configured one could take no action at all, which
/// in practice gets "fixed" by adding a wildcard entry and then nobody thinks about it again. A
/// configuration that means to restrict says which instruments it allows.
/// </para>
/// <para>
/// Two limits of the same kind and scope are refused. A duplicate is not a tightening: whichever the
/// engine happens to evaluate first would win, and which one that is would depend on ordering
/// nobody controls.
/// </para>
/// </remarks>
public sealed record LimitSet
{
    private readonly List<Limit> _limits;
    private readonly HashSet<string> _allowedInstruments;

    private LimitSet(List<Limit> limits, HashSet<string> allowedInstruments, bool refusesEverything)
    {
        _limits = limits;
        _allowedInstruments = allowedInstruments;
        RefusesEverything = refusesEverything;
    }

    /// <summary>No limits and no allow-list. Only for tests and for an unconfigured installation.</summary>
    public static LimitSet Empty { get; } =
        new([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);

    /// <summary>
    /// The set to use when the configured limits cannot be read. It refuses everything.
    /// </summary>
    /// <remarks>
    /// The same shape as <c>PolicyContext.FailClosed</c>, and for the same reason. A system that
    /// cannot determine its own ceilings must not act: returning <see cref="Empty"/> on a read
    /// failure would turn "the configuration is unavailable" into "there are no limits", which is
    /// the most dangerous possible misreading of the same fact.
    /// </remarks>
    public static LimitSet FailClosed { get; } =
        new([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), true);

    /// <summary>True when this set could not be read and therefore refuses every action.</summary>
    public bool RefusesEverything { get; }

    public IReadOnlyList<Limit> Limits => _limits;

    /// <summary>Instruments that may be acted on. Empty means no restriction.</summary>
    public IReadOnlyCollection<string> AllowedInstruments => _allowedInstruments;

    public bool RestrictsInstruments => _allowedInstruments.Count > 0;

    public static LimitSet Create(IEnumerable<Limit> limits, IEnumerable<string>? allowedInstruments = null)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var materialised = limits.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var limit in materialised)
        {
            ArgumentNullException.ThrowIfNull(limit);

            var key = $"{limit.Kind}/{limit.Capability?.ToString() ?? "*"}";

            if (!seen.Add(key))
            {
                throw new DomainValidationException(
                    nameof(limits),
                    $"Two limits are configured for {key}. Which one binds would depend on evaluation " +
                    "order, so the configuration is refused rather than resolved arbitrarily.");
            }
        }

        var instruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (allowedInstruments is not null)
        {
            foreach (var instrument in allowedInstruments)
            {
                if (!string.IsNullOrWhiteSpace(instrument))
                {
                    instruments.Add(instrument.Trim());
                }
            }
        }

        return new LimitSet(materialised, instruments, false);
    }

    /// <summary>
    /// The limit of a kind that applies to <paramref name="capability"/>, if one is configured.
    /// </summary>
    /// <remarks>
    /// A capability-scoped limit wins over a global one of the same kind, because the narrower
    /// statement is the more deliberate one.
    /// </remarks>
    public Limit? For(LimitKind kind, Capability capability) =>
        _limits.Find(limit => limit.Kind == kind && limit.Capability == capability)
        ?? _limits.Find(limit => limit.Kind == kind && limit.Capability is null);

    public bool Allows(string? instrument) =>
        !RestrictsInstruments ||
        (instrument is not null && _allowedInstruments.Contains(instrument));

    public override string ToString() =>
        $"{_limits.Count} limits, {(RestrictsInstruments ? $"{_allowedInstruments.Count} instruments" : "no instrument restriction")}";
}
