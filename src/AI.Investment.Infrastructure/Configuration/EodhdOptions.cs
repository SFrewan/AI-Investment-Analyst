using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>
/// Configuration for the EODHD end-of-day price connector.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The API key is a secret and has no default.</strong> It belongs in the user-secrets
/// store in development and in an environment variable or managed secret store in production, and
/// it is never written to a tracked configuration file. See <c>docs/SECURITY.md</c>.
/// </para>
/// <para>
/// <see cref="Enabled"/> defaults to <c>false</c>, so an installation that has configured nothing
/// gets no EODHD connector at all: the gateway refuses runs for that source with a named reason and
/// records the refusal. Failing closed and loudly beats a connector that silently answers nothing.
/// </para>
/// <para>
/// <strong><see cref="Exchanges"/> is the reason this class is longer than the EDGAR one.</strong>
/// EODHD's end-of-day rows carry a trading <em>date</em> and no times: no session close, no
/// timezone, and no statement of when the row became public. The platform's provenance needs two
/// instants for every observation, and one of them - <c>PublishedAtUtc</c> - is what every
/// point-in-time judgement in the system is made from. Those instants are facts about a
/// <em>market</em>, not about EODHD, so they are stated here by the operator, per exchange, and
/// carried onto every observation as a caveat. A connector that filled them in from a table of its
/// own would be asserting trading hours nobody configured; one that used retrieval time would let
/// a backtest see a price before it existed. An exchange that is not configured is refused.
/// </para>
/// </remarks>
public sealed class EodhdOptions : IValidatableObject
{
    public const string SectionName = "Providers:Eodhd";

    /// <summary>The default host. Overridable so a test or a proxy can point elsewhere.</summary>
    public const string DefaultBaseAddress = "https://eodhd.com/";

    /// <summary>Whether the connector is registered at all. False unless deliberately enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The EODHD API token. <strong>A secret.</strong> Required when <see cref="Enabled"/>.
    /// </summary>
    /// <remarks>
    /// Never logged, never returned in an exception message, never serialised. The connector
    /// redacts it out of anything it throws, and a test asserts that it does.
    /// </remarks>
    [MaxLength(200)]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The EODHD host.</summary>
    public string BaseAddress { get; init; } = DefaultBaseAddress;

    /// <summary>
    /// Requests per minute this installation will make. Enforced by the gateway's existing rate
    /// limiter through the connector's declared quota.
    /// </summary>
    /// <remarks>
    /// Conservative by default. EODHD's ceiling depends on the plan, which this repository cannot
    /// know; a default that assumed the largest plan would be a promise on somebody else's behalf.
    /// </remarks>
    [Range(1, 2000)]
    public int MaxRequestsPerMinute { get; init; } = 60;

    /// <summary>
    /// The markets whose sessions this installation has stated. Required when <see cref="Enabled"/>.
    /// </summary>
    public IReadOnlyList<ExchangeSessionOptions> Exchanges { get; init; } = [];

    /// <summary>The licensing note recorded against the source in the registry.</summary>
    /// <remarks>
    /// Required when enabled. EODHD's terms depend on the subscription, and a registry entry that
    /// guessed would record a licensing claim nobody made.
    /// </remarks>
    [MaxLength(2000)]
    public string LicensingNotes { get; init; } = string.Empty;

    /// <summary>Whether this installation's subscription permits redistributing the data.</summary>
    public bool RedistributionAllowed { get; init; }

    /// <summary>How long raw payloads may be retained, in days. Null for unlimited.</summary>
    public int? RetentionDays { get; init; }

    /// <summary>The stated session for an exchange code, or null when nobody stated one.</summary>
    public ExchangeSessionOptions? Session(string exchangeCode)
    {
        if (string.IsNullOrWhiteSpace(exchangeCode))
        {
            return null;
        }

        foreach (var exchange in Exchanges)
        {
            if (string.Equals(exchange.Code, exchangeCode, StringComparison.OrdinalIgnoreCase))
            {
                return exchange;
            }
        }

        return null;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            yield return new ValidationResult(
                "An EODHD API key is required when the connector is enabled. Set it in the " +
                "user-secrets store or the environment variable " +
                "Providers__Eodhd__ApiKey - never in a tracked configuration file.",
                [nameof(ApiKey)]);
        }

        if (string.IsNullOrWhiteSpace(LicensingNotes))
        {
            yield return new ValidationResult(
                "Licensing notes are required when the EODHD connector is enabled. The terms " +
                "depend on this installation's subscription, and a registry entry that guessed " +
                "would record a licensing claim nobody made.",
                [nameof(LicensingNotes)]);
        }

        if (Exchanges.Count == 0)
        {
            yield return new ValidationResult(
                "At least one exchange session must be stated when the EODHD connector is " +
                "enabled. End-of-day rows carry a date and no times, so the session close and the " +
                "publication delay are facts about the market that the operator states; without " +
                "them every row would be refused.",
                [nameof(Exchanges)]);
        }

        if (!Uri.TryCreate(BaseAddress, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            yield return new ValidationResult(
                "The EODHD base address must be an absolute https URL. The API key travels in the " +
                "query string, so plaintext http would put it on the wire.",
                [nameof(BaseAddress)]);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var exchange in Exchanges)
        {
            foreach (var result in exchange.Problems())
            {
                yield return result;
            }

            if (!seen.Add(exchange.Code ?? string.Empty))
            {
                yield return new ValidationResult(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Exchange '{exchange.Code}' is stated more than once. Two sessions for one " +
                        $"market would make which one applied depend on list order."),
                    [nameof(Exchanges)]);
            }
        }
    }
}

/// <summary>
/// What the operator states about one market's trading day.
/// </summary>
/// <remarks>
/// Both values are the operator's claim about an exchange, not EODHD's. They are carried onto every
/// observation as a caveat so that a reader of the ledger can see the assumption rather than
/// inferring it.
/// </remarks>
public sealed class ExchangeSessionOptions
{
    /// <summary>The EODHD exchange suffix, without the dot - <c>US</c>, <c>LSE</c>, <c>TO</c>.</summary>
    [MaxLength(20)]
    public string? Code { get; init; }

    /// <summary>
    /// The time of day, in UTC, at which a regular session on this exchange closes.
    /// </summary>
    /// <remarks>
    /// A single value, deliberately. A market whose close moves with daylight saving needs two
    /// entries or a later block that reads a calendar; encoding a timezone rule here would be this
    /// connector inventing a trading calendar, which is the thing it exists not to do. A stated
    /// close that is an hour out is visible in configuration; an inferred one is not.
    /// </remarks>
    public TimeSpan SessionCloseUtc { get; init; }

    /// <summary>
    /// How long after the session closes this installation treats the day's close as public.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EODHD does not state when a row was published, so this is the operator's conservative
    /// stand-in. <strong>Erring late is safe and erring early is not:</strong> a publication time
    /// earlier than the truth lets a backtest act on a price before anybody could have seen it,
    /// which is precisely the bias Phase 7's evidence guard exists to prevent. The default is
    /// deliberately generous.
    /// </para>
    /// </remarks>
    public TimeSpan PublicationDelay { get; init; } = TimeSpan.FromHours(4);

    internal IEnumerable<ValidationResult> Problems()
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            yield return new ValidationResult(
                "An exchange session needs the EODHD exchange code it describes.",
                [nameof(Code)]);
        }

        if (SessionCloseUtc < TimeSpan.Zero || SessionCloseUtc >= TimeSpan.FromDays(1))
        {
            yield return new ValidationResult(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The session close for '{Code}' must be a time of day between 00:00 and 23:59."),
                [nameof(SessionCloseUtc)]);
        }

        if (PublicationDelay < TimeSpan.Zero || PublicationDelay > TimeSpan.FromDays(7))
        {
            yield return new ValidationResult(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The publication delay for '{Code}' must be between zero and seven days. A " +
                    $"negative delay would claim a close was public before the session ended."),
                [nameof(PublicationDelay)]);
        }
    }
}
