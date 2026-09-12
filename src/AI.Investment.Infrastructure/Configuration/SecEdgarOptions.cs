using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>
/// Configuration for the SEC EDGAR connector.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here is hard-coded and nothing here is a secret.</strong> EDGAR requires no API
/// key; what it does require is that every request identify who is making it, through a
/// <c>User-Agent</c> carrying an application name and a contact address. That is a term of the
/// SEC's fair-access policy rather than a courtesy, so the connector refuses to run without it -
/// but a contact address is still deployment configuration, not source code, and putting a real
/// person's e-mail in a repository would be both wrong and useless the moment it changed.
/// </para>
/// <para>
/// <see cref="Enabled"/> defaults to <c>false</c>, so an installation that has not supplied a
/// contact address gets no EDGAR connector at all. The ingestion gateway then refuses runs for
/// that source with <c>ingestion.provider-available@1</c> and writes the refusal to the ledger -
/// visible, explained, and safe. Failing closed and loudly beats defaulting to a placeholder
/// identity the SEC would be entitled to block.
/// </para>
/// </remarks>
public sealed class SecEdgarOptions : IValidatableObject
{
    public const string SectionName = "Providers:SecEdgar";

    /// <summary>
    /// The SEC's published fair-access ceiling. Not a tuning knob.
    /// </summary>
    public const int FairAccessRequestsPerSecond = 10;

    /// <summary>Whether the connector is registered at all. False unless deliberately enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// The name this installation identifies itself by. Required when <see cref="Enabled"/>.
    /// </summary>
    [MaxLength(120)]
    public string ApplicationName { get; init; } = string.Empty;

    /// <summary>
    /// A monitored contact address. Required when <see cref="Enabled"/>.
    /// </summary>
    /// <remarks>
    /// The SEC uses it to reach whoever is responsible if a client misbehaves. An unmonitored or
    /// invented address defeats the purpose of the requirement.
    /// </remarks>
    [MaxLength(200)]
    [EmailAddress]
    public string ContactEmail { get; init; } = string.Empty;

    /// <summary>The EDGAR data host.</summary>
    /// <remarks>
    /// The JSON API: submissions, company facts and frames. It is NOT where filing documents live -
    /// see <see cref="ArchiveBaseAddress"/>.
    /// </remarks>
    public string BaseAddress { get; init; } = "https://data.sec.gov/";

    /// <summary>The EDGAR archive host, which serves filing documents.</summary>
    /// <remarks>
    /// <para>
    /// <strong>EDGAR addresses its JSON API and its document archive on different hosts, and this is
    /// the second of them.</strong> <see cref="BaseAddress"/> serves <c>submissions/CIK…json</c> and
    /// <c>api/xbrl/…</c>; the <c>Archives/edgar/data/…</c> tree that holds the documents themselves
    /// is served from here. A request built for one host against the other is a well-formed path
    /// pointed at a service that does not serve it - which is exactly what happened to the first
    /// filing-document batch, and cost five authorisation units to discover.
    /// </para>
    /// <para>
    /// It is configuration rather than a constant for the same reason the data host is: a host is a
    /// deployment fact, and hard-coding one inside a connector is how a change of address becomes a
    /// code change. Neither host carries a credential - EDGAR requires none - so this is a setting
    /// and not a secret.
    /// </para>
    /// <para>
    /// <strong>It is not a second source.</strong> Both hosts are the SEC's EDGAR system, both are
    /// reached by the same registered <c>HttpClient</c> under the same fair-access identity and the
    /// same declared quota, and everything acquired from either is recorded under
    /// <c>sec-edgar</c>. This names a transport endpoint, nothing more.
    /// </para>
    /// </remarks>
    public string ArchiveBaseAddress { get; init; } = "https://www.sec.gov/";

    /// <summary>
    /// Requests per second this installation will make. Clamped to the SEC's published ceiling.
    /// </summary>
    [Range(1, FairAccessRequestsPerSecond)]
    public int MaxRequestsPerSecond { get; init; } = FairAccessRequestsPerSecond;

    /// <summary>
    /// The <c>User-Agent</c> the connector sends, in the form the SEC asks for.
    /// </summary>
    public string UserAgent => $"{ApplicationName} {ContactEmail}".Trim();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(ApplicationName))
        {
            yield return new ValidationResult(
                "An application name is required when the SEC EDGAR connector is enabled. The SEC's " +
                "fair-access policy requires every request to identify its origin.",
                [nameof(ApplicationName)]);
        }

        if (string.IsNullOrWhiteSpace(ContactEmail))
        {
            yield return new ValidationResult(
                "A contact e-mail is required when the SEC EDGAR connector is enabled. It is how the " +
                "SEC reaches whoever is responsible for this client.",
                [nameof(ContactEmail)]);
        }

        if (!Uri.TryCreate(BaseAddress, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            yield return new ValidationResult(
                "The EDGAR base address must be an absolute https URL.",
                [nameof(BaseAddress)]);
        }

        // The same rule, for the same reason. A relative archive host cannot be resolved against
        // anything, and a plaintext one would carry a public-record request over an unprotected
        // connection. Both are worth refusing at startup rather than discovering on the wire.
        if (!Uri.TryCreate(ArchiveBaseAddress, UriKind.Absolute, out var archiveUri) ||
            !string.Equals(archiveUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            yield return new ValidationResult(
                "The EDGAR archive base address must be an absolute https URL.",
                [nameof(ArchiveBaseAddress)]);
        }
    }
}
