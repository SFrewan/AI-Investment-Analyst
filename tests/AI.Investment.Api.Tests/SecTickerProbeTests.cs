using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Five free SEC requests, spent to answer one question and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The question.</strong> EDGAR strips a company's <c>tickers</c> array on deregistration,
/// which is why 110 of the 115 members that died cannot be priced. A company's own filings do not
/// disappear, and since the cover-page XBRL rule a 10-K carries <c>dei:TradingSymbol</c> and
/// <c>dei:SecurityExchangeName</c> as tagged facts. If those tags are present and readable for the
/// members that died, historical identity is recoverable from issuer-stated evidence and Path B is
/// real. If they are not, Path B is a dead end and this stage stops rather than improvising.
/// </para>
/// <para>
/// <strong>Why five calls are enough to answer it.</strong> The accession numbers come from
/// submissions documents already in the archive, so they cost nothing. Each call fetches the
/// prefix of one company's primary filing document - the cover page is the first thing in a 10-K,
/// so a prefix is exactly the right read and a multi-megabyte exhibit never arrives. Five
/// companies chosen to span the range - large and tiny, listed and OTC, died early and died late -
/// distinguish "the tag is there" from "the tag is there for the easy ones", which is the
/// distinction that decides whether 110 recoveries are worth attempting.
/// </para>
/// <para>
/// <strong>What it does not do.</strong> No price, no split, no dividend, no EODHD call. Nothing
/// is written to the observation store, the manifest or the identity artefact: the probe reports
/// evidence and stops. A ticker found here is not applied to any member by this stage.
/// </para>
/// </remarks>
public sealed class SecTickerProbeTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_SEC_TICKER_PROBE";

    /// <summary>The hard ceiling, enforced by the transport rather than by care.</summary>
    private const int CallBudget = 5;

    /// <summary>Bytes of each filing to read. The cover page is at the front.</summary>
    private const int PrefixBytes = 2 * 1024 * 1024;

    private const string ArchiveBase = "https://www.sec.gov/Archives/edgar/data/";

    private static readonly string[] Tags =
    [
        "TradingSymbol",
        "Security12bTitle",
        "SecurityExchangeName",
    ];

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SecTickerProbeTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Five_filings_say_whether_a_dead_company_still_states_its_own_ticker()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The identity probe is off. Set {GateVariable}=1 to run it. It makes at most " +
            $"{CallBudget} free SEC requests and no billable call.");

        var contact = Environment.GetEnvironmentVariable("AIINV_SEC_CONTACT");

        Skip.If(
            string.IsNullOrWhiteSpace(contact),
            "AIINV_SEC_CONTACT is not set and EDGAR fair access requires a contact address.");

        var root = _factory.Services;

        // ---- who to ask, and why them ---------------------------------------------------------

        var candidates = await CandidatesAsync(root);

        Skip.If(
            candidates.Count == 0,
            "No archived submissions document was found for any unpriceable dropout, so there is " +
            "no accession to probe without spending calls the budget does not have.");

        var chosen = Spread(candidates, CallBudget);

        // ---- the calls, ceiling enforced by the transport --------------------------------------

        using var budget = new BoundedCalls(CallBudget)
        {
            InnerHandler = new HttpClientHandler(),
        };

        using var client = new HttpClient(budget);

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "AI-Investment-Analyst/1.0 (" + contact + ")");

        var findings = new List<Finding>();
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;

        foreach (var candidate in chosen)
        {
            attempted++;

            try
            {
                var prefix = await PrefixAsync(client, candidate.Url);

                succeeded++;

                findings.Add(Read(candidate, prefix));
            }
            catch (HttpRequestException exception)
            {
                failed++;

                findings.Add(candidate.Failed(exception.Message));
            }
            catch (TaskCanceledException)
            {
                failed++;

                findings.Add(candidate.Failed("the request timed out"));
            }

            // EDGAR fair access: well inside 10 requests a second, and there are only five.
            await Task.Delay(300);
        }

        // ---- what it establishes ---------------------------------------------------------------

        var withSymbol = findings.Count(f => f.Symbol is not null);

        var report = Compose(findings, attempted, succeeded, failed, withSymbol, candidates.Count);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "sec-ticker-probe.md"),
            report);

        _output.WriteLine(report);

        Assert.True(attempted <= CallBudget, $"{attempted} calls against a ceiling of {CallBudget}.");
        Assert.Equal(attempted, succeeded + failed);
        Assert.Equal(chosen.Count, findings.Count);

        // The probe reports; it does not decide. A finding of zero is a result, not a failure -
        // it is the evidence that Path B is a dead end, and the stage must stop rather than
        // improvise a replacement.
        Assert.All(findings, f => Assert.NotNull(f.Evidence));
    }

    // ---- choosing ---------------------------------------------------------------------------------

    /// <summary>
    /// Unpriceable dropouts whose submissions document is already archived, with an accession.
    /// </summary>
    private static async Task<List<Candidate>> CandidatesAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json")));

        var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        var unpriceable = identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Where(m => !m.GetProperty("Ready").GetBoolean())
            .Select(m => m.GetProperty("Cik").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        var wanted = new List<(string Cik, string Name, string LastCohort)>();

        foreach (var member in manifest.RootElement.GetProperty("Members").EnumerateArray())
        {
            var cik = member.GetProperty("Cik").GetString() ?? string.Empty;
            var cohorts = member.GetProperty("Cohorts").EnumerateArray()
                .Select(c => c.GetString() ?? string.Empty)
                .ToList();

            // A dropout that cannot be priced: the population this probe is about.
            if (cohorts.Count >= 5 || !unpriceable.Contains(cik))
            {
                continue;
            }

            wanted.Add((
                cik,
                member.GetProperty("Name").GetString() ?? "(unnamed)",
                cohorts.Count == 0 ? string.Empty : cohorts.Max(StringComparer.Ordinal)!));
        }

        var runs = await context.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.Category == DataCategory.CompanyProfile)
            .ToListAsync();

        var latest = new Dictionary<string, IngestionRun>(StringComparer.Ordinal);

        foreach (var run in runs
            .Where(r => r.Outcome == IngestionOutcome.Succeeded)
            .OrderBy(r => r.StartedAtUtc))
        {
            latest[run.Request.Subject.Identifier ?? string.Empty] = run;
        }

        var candidates = new List<Candidate>();

        foreach (var (cik, name, lastCohort) in wanted)
        {
            if (!latest.TryGetValue(cik, out var run))
            {
                continue;
            }

            foreach (var hash in run.Artifacts)
            {
                var payload = await archive.RetrieveAsync(hash);

                if (payload is null)
                {
                    continue;
                }

                var filing = LatestAnnualFiling(payload);

                if (filing is null)
                {
                    continue;
                }

                candidates.Add(new Candidate(
                    cik,
                    name,
                    lastCohort,
                    filing.Value.Accession,
                    filing.Value.Form,
                    filing.Value.FilingDate,
                    Url(cik, filing.Value.Accession, filing.Value.Document)));

                break;
            }
        }

        return candidates
            .OrderBy(c => c.LastCohort, StringComparer.Ordinal)
            .ThenBy(c => c.Cik, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Takes <paramref name="count"/> evenly across the ordered list rather than the first few.
    /// </summary>
    /// <remarks>
    /// The list is ordered by when the company stopped appearing, so an even spread asks about
    /// companies that died in different years. Taking the first five would ask five versions of
    /// the same question and answer it for 2021 only.
    /// </remarks>
    private static List<Candidate> Spread(List<Candidate> candidates, int count)
    {
        if (candidates.Count <= count)
        {
            return candidates;
        }

        var step = (double)candidates.Count / count;

        return Enumerable.Range(0, count)
            .Select(i => candidates[(int)Math.Floor(i * step)])
            .ToList();
    }

    // ---- reading ----------------------------------------------------------------------------------

    private static async Task<string> PrefixAsync(HttpClient client, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();

        var buffer = new byte[PrefixBytes];
        var read = 0;

        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read));

            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>The cover-page facts, if the filing tagged them.</summary>
    private static Finding Read(Candidate candidate, string prefix)
    {
        var found = new List<string>();
        string? symbol = null;
        string? exchange = null;

        foreach (var tag in Tags)
        {
            var match = Regex.Match(
                prefix,
                Pattern(tag),
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(5));

            if (!match.Success)
            {
                // The tag may be present with the value carried by a nested span.
                if (prefix.Contains(tag, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(tag + " (tagged, value not in the prefix)");
                }

                continue;
            }

            var value = match.Groups["value"].Value.Trim();

            found.Add(tag + " = " + value);

            if (string.Equals(tag, "TradingSymbol", StringComparison.Ordinal))
            {
                symbol = value;
            }

            if (string.Equals(tag, "SecurityExchangeName", StringComparison.Ordinal))
            {
                exchange = value;
            }
        }

        var evidence = found.Count == 0
            ? "no cover-page identity tag appears in the first "
                + (PrefixBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)
                + "MB of the filing"
            : string.Join("; ", found);

        return new Finding(candidate, symbol, exchange, evidence, Failure: null);
    }

    private static (string Accession, string Document, string Form, string FilingDate)?
        LatestAnnualFiling(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);

        if (!document.RootElement.TryGetProperty("filings", out var filings) ||
            !filings.TryGetProperty("recent", out var recent))
        {
            return null;
        }

        var forms = Column(recent, "form");
        var accessions = Column(recent, "accessionNumber");
        var documents = Column(recent, "primaryDocument");
        var dates = Column(recent, "filingDate");

        var count = new[] { forms.Count, accessions.Count, documents.Count, dates.Count }.Min();

        // The most recent annual report: the cover page a 10-K carries is the richest, and the
        // last one filed is the closest statement to the moment the company stopped filing.
        for (var i = 0; i < count; i++)
        {
            if (!forms[i].StartsWith("10-K", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(documents[i]))
            {
                continue;
            }

            return (accessions[i], documents[i], forms[i], dates[i]);
        }

        return null;
    }

    /// <summary>The cover-page tag, as a pattern over the filing's inline XBRL.</summary>
    /// <remarks>
    /// Built from pieces rather than written as one verbatim literal: a regex full of escaped
    /// quotes inside a C# string is the kind of line that is read as correct and is not.
    /// </remarks>
    private static string Pattern(string tag) =>
        "name\\s*=\\s*\"(?:dei:)?" + tag + "\"[^>]*>(?<value>[^<]{1,64})<";

    private static List<string> Column(JsonElement recent, string name) =>
        recent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : [];

    private static string Url(string cik, string accession, string document) =>
        ArchiveBase
        + cik.TrimStart('0')
        + "/"
        + accession.Replace("-", string.Empty, StringComparison.Ordinal)
        + "/"
        + document;

    // ---- reporting --------------------------------------------------------------------------------

    private static string Compose(
        List<Finding> findings,
        int attempted,
        int succeeded,
        int failed,
        int withSymbol,
        int candidatePool)
    {
        var report = new StringBuilder();

        report.AppendLine("# Historical ticker probe - five free SEC requests");
        report.AppendLine();
        report.AppendLine("**No EODHD call. No price, split or dividend. Nothing was written to the");
        report.AppendLine("observation store, the manifest or the identity table.** A ticker found");
        report.AppendLine("here is evidence, and is not applied to any member by this stage.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Candidate pool (unpriceable dropouts with an archived filing) | {candidatePool} |"));
        report.AppendLine(Universe.Inv($"| SEC calls attempted | {attempted} of {CallBudget} |"));
        report.AppendLine(Universe.Inv($"| Succeeded / failed | {succeeded} / {failed} |"));
        report.AppendLine(Universe.Inv($"| **Filings stating their own trading symbol** | **{withSymbol} of {attempted}** |"));
        report.AppendLine(Universe.Inv($"| EODHD calls | 0 |"));
        report.AppendLine();

        report.AppendLine("## What each filing said");
        report.AppendLine();
        report.AppendLine("| CIK | Company | Last cohort | Form | Filed | Symbol | Exchange | Evidence |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (var f in findings)
        {
            report.AppendLine(Universe.Inv(
                $"| `{f.Candidate.Cik}` | {f.Candidate.Name} | {f.Candidate.LastCohort} | {f.Candidate.Form} | {f.Candidate.FilingDate} | {f.Symbol ?? "-"} | {f.Exchange ?? "-"} | {f.Failure ?? f.Evidence} |"));
        }

        report.AppendLine();
        report.AppendLine("## What this establishes");
        report.AppendLine();

        if (attempted == 0)
        {
            report.AppendLine("Nothing: no call was made.");
        }
        else if (withSymbol == attempted)
        {
            report.AppendLine(Universe.Inv(
                $"Every one of the {attempted} filings probed states its own trading symbol. On this evidence the recovery method works across the range sampled, and a full attempt over the 110 unpriceable dropouts is worth authorising."));
        }
        else if (withSymbol == 0)
        {
            report.AppendLine(Universe.Inv(
                $"None of the {attempted} filings probed states a readable trading symbol. On this evidence the method does not recover historical identity, Path B is a dead end as designed, and this stage stops rather than improvising a replacement universe."));
        }
        else
        {
            report.AppendLine(Universe.Inv(
                $"{withSymbol} of {attempted} filings state their own trading symbol. That is partial: the method works for some members and not others, so a full attempt would recover some of the 110 and leave the rest unmatched. Whether partial recovery is worth the calls is a decision, not a finding."));
        }

        report.AppendLine();
        report.AppendLine("A symbol found here is the issuer's own statement in a filing that cannot");
        report.AppendLine("be withdrawn. That is a different kind of evidence from a name match");
        report.AppendLine("against a vendor's delisted list, which EDGAR contradicted 36 times out of");
        report.AppendLine("the 37 it could adjudicate.");

        return report.ToString();
    }

    private sealed record Candidate(
        string Cik,
        string Name,
        string LastCohort,
        string Accession,
        string Form,
        string FilingDate,
        string Url)
    {
        public Finding Failed(string reason) =>
            new(this, null, null, "the filing could not be retrieved", reason);
    }

    private sealed record Finding(
        Candidate Candidate,
        string? Symbol,
        string? Exchange,
        string Evidence,
        string? Failure);
}
