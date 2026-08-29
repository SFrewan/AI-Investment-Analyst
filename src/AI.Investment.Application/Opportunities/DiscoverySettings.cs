using AI.Investment.Domain.Opportunities.Equity;

namespace AI.Investment.Application.Opportunities;

/// <summary>
/// What the price-recovery discoverer reads, in what currency, and over how much history.
/// </summary>
/// <remarks>
/// <para>
/// A plain settings object rather than an options binding, because the application layer depends on
/// the dependency-injection abstraction and nothing else - no configuration provider, no binder, no
/// <c>IOptions</c>. Infrastructure binds its own options type and hands one of these over, which is
/// the same shape every other configured application service in this solution has.
/// </para>
/// <para>
/// <see cref="PriceAttribute"/> defaults to the attribute the market-data normaliser writes and the
/// validation run reads. The three are the same string, and a test asserts it: a discoverer reading
/// one attribute while the measurement reads another would produce opportunities that are never
/// admissible, and the symptom would be an empty report rather than an error.
/// </para>
/// </remarks>
public sealed record DiscoverySettings
{
    /// <summary>The shipped settings.</summary>
    public static DiscoverySettings Standard { get; } = new();

    /// <summary>The observation attribute closing prices are read from.</summary>
    public string PriceAttribute { get; init; } = "security.close";

    /// <summary>The currency an equity candidate's economics are denominated in.</summary>
    public string CurrencyCode { get; init; } = "USD";

    /// <summary>
    /// The most recent sessions the screen looks at, and the sessions it cites as evidence.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. An opportunity cites every close its base rate was counted over, and an
    /// unbounded window would grow that citation list without bound as the platform runs - which is
    /// a slow way to make an audit trail unreadable. A hundred and twenty sessions is roughly six
    /// months, which is enough for the shipped rule's sixty-session history and a run of trials
    /// after it.
    /// </remarks>
    public int MaxSessions { get; init; } = 120;

    /// <summary>The screen's own parameters.</summary>
    public PriceRecoveryParameters Rule { get; init; } = PriceRecoveryParameters.Standard;
}
