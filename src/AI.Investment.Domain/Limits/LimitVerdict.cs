namespace AI.Investment.Domain.Limits;

/// <summary>The limit engine's answer: permitted, or refused with every reason.</summary>
public sealed record LimitVerdict
{
    private readonly List<LimitBreach> _breaches;

    private LimitVerdict(List<LimitBreach> breaches) => _breaches = breaches;

    /// <summary>Nothing was exceeded.</summary>
    public static LimitVerdict Allowed { get; } = new([]);

    public IReadOnlyList<LimitBreach> Breaches => _breaches;

    public bool IsAllowed => _breaches.Count == 0;

    public static LimitVerdict Refused(IEnumerable<LimitBreach> breaches)
    {
        ArgumentNullException.ThrowIfNull(breaches);

        var materialised = breaches.ToList();

        return materialised.Count == 0 ? Allowed : new LimitVerdict(materialised);
    }

    /// <summary>A single line naming every ceiling that stopped the action.</summary>
    public string Explain() =>
        IsAllowed
            ? "Within every configured limit."
            : "Refused by " + string.Join("; ", _breaches);

    public override string ToString() => Explain();
}
