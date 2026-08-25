using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Ingestion;

/// <summary>
/// The rate a provider permits, as a number of requests within a rolling window.
/// </summary>
/// <remarks>
/// <para>
/// A declared limit rather than a discovered one. The platform is required not to work around a
/// provider's restrictions, and the way to honour that is to state the limit where the scheduler
/// can see it, not to fetch until the provider starts refusing. Backing off after a 429 is
/// reacting to enforcement; staying under a declared quota is complying with terms.
/// </para>
/// <para>
/// A property of the API rather than of the source's trustworthiness, which is why it lives on
/// <see cref="ProviderCapabilities"/> and not on <see cref="Sources.DataSource"/>. The same
/// regulator's filings might be reachable through a rate-limited API and an unlimited bulk file;
/// what the platform believes about the regulator does not change between them.
/// </para>
/// </remarks>
public sealed record ProviderQuota
{
    private ProviderQuota(int maxRequests, TimeSpan window)
    {
        MaxRequests = maxRequests;
        Window = window;
    }

    public int MaxRequests { get; }

    public TimeSpan Window { get; }

    /// <summary>The minimum spacing between requests that keeps the quota satisfied evenly.</summary>
    public TimeSpan MinimumSpacing => Window / MaxRequests;

    public static ProviderQuota PerSecond(int maxRequests) =>
        Create(maxRequests, TimeSpan.FromSeconds(1));

    public static ProviderQuota PerMinute(int maxRequests) =>
        Create(maxRequests, TimeSpan.FromMinutes(1));

    public static ProviderQuota PerDay(int maxRequests) =>
        Create(maxRequests, TimeSpan.FromDays(1));

    public static ProviderQuota Create(int maxRequests, TimeSpan window)
    {
        if (maxRequests < 1)
        {
            throw new DomainValidationException(
                nameof(maxRequests),
                $"A quota must permit at least one request. Received {maxRequests}.");
        }

        if (window <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(window),
                $"A quota window must be positive. Received {window}.");
        }

        return new ProviderQuota(maxRequests, window);
    }

    public override string ToString() => $"{MaxRequests} per {Window}";
}
