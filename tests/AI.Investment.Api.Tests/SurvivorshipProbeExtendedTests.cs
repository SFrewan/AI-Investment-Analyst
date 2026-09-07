using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Ten more free SEC requests, to narrow an interval that missed its bar by one point.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a second round rather than a decision.</strong> The first ten recovered eight, an
/// eighty per cent rate whose 95% lower bound came out at 49% against a pre-registered bar of 50%.
/// Moving the bar would have made the bar meaningless; deciding against recovery on a
/// one-point miss would have thrown away a strong signal. Ten more filings is the only move that
/// costs nothing and settles it either way.
/// </para>
/// <para>
/// <strong>The method is not changed, and neither is the bar.</strong> Same Wilson interval, same
/// <see cref="SurvivorshipProbeSelection.JustifiesFullRecovery"/>, same denominator. A second
/// figure is reported alongside for the hypothesis below, clearly labelled and NOT substituted for
/// the first - because changing what a number means between rounds is how a result gets talked
/// into existence.
/// </para>
/// <para>
/// <strong>The hypothesis, tested for free.</strong> Two of the first ten filings carried no
/// ticker: Avon, filed after the company was acquired, and SPYR, a shell. If those are companies
/// with no listed security rather than filings the method failed on, they are not failures at all
/// - there was nothing to recover. A 10-K states this on its own cover page ("Securities
/// registered pursuant to Section 12(b) of the Act: None"), so it can be read from the very
/// document each call already fetches, with no extra request.
/// </para>
/// </remarks>
public sealed class SurvivorshipProbeExtendedTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_SURVIVORSHIP_PROBE_2";

    private const int CallBudget = 10;

    private const int PrefixBytes = 8 * 1024 * 1024;

    private const string Host = "www.sec.gov";

    private const string ArchiveBase = "https://" + Host + "/Archives/edgar/data/";

    /// <summary>
    /// The first round's result, from <c>artifacts/verify/survivorship-probe-extended.md</c>.
    /// </summary>
    /// <remarks>
    /// Stated rather than re-parsed out of the prose of a report, and pinned by a test. The two
    /// non-recoveries there are carried forward as <see cref="Outcome.ListedStatusUnresolved"/>
    /// and NOT as proven absences of a listed security: their documents were not retained, and
    /// re-reading them would cost calls this round is not authorised to spend.
    /// </remarks>
    private const int PriorRecovered = 8;

    private const int PriorNotRecovered = 2;

    /// <summary>Reports whose CIKs name members that must not be read again.</summary>
    private static readonly string[] PriorReports =
    [
        "survivorship-probe-extended.md",
        "sec-ticker-probe.md",
        "sec-ticker-refinement.md",
    ];

    private static readonly string[] Tags =
    [
        "TradingSymbol",
        "Security12bTitle",
        "SecurityExchangeName",
    ];

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SurvivorshipProbeExtendedTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Ten_more_filings_narrow_the_interval_without_moving_the_bar()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The second probe round is off. Set {GateVariable}=1 to run it. It makes at most " +
            $"{CallBudget} free SEC requests and no billable call.");

        var contact = Environment.GetEnvironmentVariable("AIINV_SEC_CONTACT");

        Skip.If(
            string.IsNullOrWhiteSpace(contact),
            "AIINV_SEC_CONTACT is not set and EDGAR fair access requires a contact address.");

        // ---- the transport, diagnosed for nothing -------------------------------------------------

        var dns = await ResolveAsync();

        // ---- who has already been read, taken from the artifacts rather than retyped --------------

        var alreadyProbed = AlreadyProbed();

        Assert.NotEmpty(alreadyProbed);

        var pool = await CandidatesAsync(_factory.Services);

        var candidates = pool
            .Where(c => !alreadyProbed.Contains(c.Cik))
            .ToList();

        Skip.If(candidates.Count == 0, "Every candidate has already been probed.");

        var chosen = SurvivorshipProbeSelection.Choose(candidates);

        // ---- the sample, frozen and written down before anything is read --------------------------

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "survivorship-probe-selection-2.md"),
            Selection(pool, candidates, chosen, alreadyProbed, dns));

        // Nobody is read twice, which is what makes the two rounds add up to twenty filings.
        Assert.All(chosen, c => Assert.DoesNotContain(c.Cik, alreadyProbed));
        Assert.Equal(chosen.Count, chosen.Select(c => c.Cik).Distinct(StringComparer.Ordinal).Count());
        Assert.True(chosen.Count <= CallBudget);

        Skip.If(
            !dns.Resolved,
            "The SEC archive host does not resolve from this machine right now, so every request "
            + "would fail before reaching SEC. The frozen selection has been written; re-run when "
            + "the host resolves. " + dns.Summary);

        // ---- the calls ------------------------------------------------------------------------------

        using var budget = new BoundedCalls(CallBudget)
        {
            InnerHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(30),
            },
        };

        using var client = new HttpClient(budget) { Timeout = TimeSpan.FromMinutes(2) };

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
                var document = await PrefixAsync(client, Url(candidate));

                succeeded++;
                findings.Add(Read(candidate, document));
            }
            catch (Exception exception) when (IsTransport(exception))
            {
                failed++;
                findings.Add(Finding.Transport(candidate, Short(exception)));
            }

            await Task.Delay(300);
        }

        // ---- what it establishes ----------------------------------------------------------------------

        var recovered = findings.Count(f => f.Outcome == Outcome.Recovered);
        var noSecurity = findings.Count(f => f.Outcome == Outcome.NoListedSecurity);
        var unresolved = findings.Count(f => f.Outcome == Outcome.ListedStatusUnresolved);
        var transport = findings.Count(f => f.Outcome == Outcome.TransportFailure);

        var report = Compose(
            pool, candidates, chosen, findings, alreadyProbed, dns,
            attempted, succeeded, failed, recovered, noSecurity, unresolved, transport);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "survivorship-probe-extended-20.md"),
            report);

        _output.WriteLine(report);

        Assert.True(attempted <= CallBudget, $"{attempted} calls against a ceiling of {CallBudget}.");
        Assert.Equal(attempted, succeeded + failed);
        Assert.Equal(findings.Count, recovered + noSecurity + unresolved + transport);

        // Transport failures are outside the denominator, in both rounds and in the total.
        Assert.Equal(failed, transport);
    }

    // ---- classification -----------------------------------------------------------------------------

    /// <summary>
    /// One filing, read into one of the four outcomes and never into two.
    /// </summary>
    private static Finding Read(SurvivorshipProbeSelection.Candidate candidate, string document)
    {
        var symbol = Value(document, "TradingSymbol");

        if (symbol is not null)
        {
            return new Finding(
                candidate,
                Outcome.Recovered,
                symbol,
                Value(document, "SecurityExchangeName"),
                Value(document, "Security12bTitle"),
                Universe.Inv(
                    $"issuer-stated on the cover page of a {candidate.Form} filed {candidate.FilingDate}"));
        }

        // No ticker. The cover page itself says whether there was one to state.
        var listed = RegisteredUnder12b(document);

        return listed switch
        {
            false => new Finding(
                candidate,
                Outcome.NoListedSecurity,
                null,
                null,
                null,
                Universe.Inv(
                    $"the cover page states no securities registered under Section 12(b); there was no ticker to recover, so this is not a failure of the method ({document.Length} characters read)")),

            _ => new Finding(
                candidate,
                Outcome.ListedStatusUnresolved,
                null,
                null,
                null,
                Universe.Inv(
                    $"no trading symbol in {document.Length} characters of `{candidate.Document}`, and the cover page does not settle whether a Section 12(b) security existed")),
        };
    }

    /// <summary>
    /// Whether the filing says it has securities registered under Section 12(b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 10-K cover page carries the heading "Securities registered pursuant to Section 12(b) of
    /// the Act:" followed either by a table of securities or by the word None. A company that has
    /// been acquired, taken private or reduced to a shell answers None - and a company with no
    /// listed security has no ticker to tag, which is a different thing from a filing that failed
    /// to tag one.
    /// </para>
    /// <para>
    /// Returns null when the heading is not found at all, because "the document does not say" must
    /// not be read as "the answer is no". That distinction is the whole point of separating outcome
    /// two from outcome three.
    /// </para>
    /// </remarks>
    private static bool? RegisteredUnder12b(string document)
    {
        var text = Regex.Replace(
            document, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(20));

        text = text.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, "\\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(20));

        var heading = Regex.Match(
            text,
            "Securities\\s+registered\\s+pursuant\\s+to\\s+Section\\s+12\\s*\\(\\s*b\\s*\\)",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(20));

        if (!heading.Success)
        {
            return null;
        }

        // The answer follows the heading. A short window, because the next heading - 12(g) - comes
        // soon after and its own answer must not be mistaken for this one.
        var start = heading.Index + heading.Length;
        var window = text[start..Math.Min(text.Length, start + 400)];

        var twelveG = window.IndexOf("12(g)", StringComparison.OrdinalIgnoreCase);

        if (twelveG >= 0)
        {
            window = window[..twelveG];
        }

        if (Regex.IsMatch(window, "\\bNone\\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(10)))
        {
            return false;
        }

        // A title, a symbol column or an exchange name means there is a listed security.
        return Regex.IsMatch(
            window,
            "Title\\s+of|Trading\\s+Symbol|Name\\s+of\\s+each\\s+exchange|New\\s+York\\s+Stock|Nasdaq|NYSE",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(10))
                ? true
                : null;
    }

    // ---- who has been read already --------------------------------------------------------------------

    /// <summary>
    /// Every CIK named in a previous probe report, read from the artifacts themselves.
    /// </summary>
    /// <remarks>
    /// Read rather than retyped. A hand-copied exclusion list is one transcription slip away from
    /// spending an authorised call re-reading a filing whose answer is already known, and the
    /// reports are the record of what was actually read.
    /// </remarks>
    private static HashSet<string> AlreadyProbed()
    {
        var ciks = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in PriorReports)
        {
            var path = Universe.RepositoryPath("artifacts", "verify", name);

            if (!File.Exists(path))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(
                File.ReadAllText(path),
                "`(?<cik>[0-9]{10})`",
                RegexOptions.None,
                TimeSpan.FromSeconds(10)))
            {
                ciks.Add(match.Groups["cik"].Value);
            }
        }

        return ciks;
    }

    // ---- the rest: unchanged from the first round -------------------------------------------------------

    private static async Task<Dns> ResolveAsync()
    {
        var attempts = new List<string>();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(Host);

                if (addresses.Length > 0)
                {
                    return new Dns(true, string.Create(
                        CultureInfo.InvariantCulture,
                        $"{Host} resolved to {addresses.Length} address(es) on attempt {attempt} of 3. No SEC request was spent doing it."));
                }

                attempts.Add(string.Create(
                    CultureInfo.InvariantCulture, $"attempt {attempt}: no addresses"));
            }
            catch (SocketException exception)
            {
                attempts.Add(string.Create(
                    CultureInfo.InvariantCulture, $"attempt {attempt}: {exception.SocketErrorCode}"));
            }

            await Task.Delay(2000);
        }

        return new Dns(false, Host + " did not resolve in 3 attempts (" + string.Join("; ", attempts) + ").");
    }

    private static async Task<List<SurvivorshipProbeSelection.Candidate>> CandidatesAsync(
        IServiceProvider root)
    {
        using var scope = root.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json")));

        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        var status = identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Where(m => !m.GetProperty("Ready").GetBoolean())
            .ToDictionary(
                m => m.GetProperty("Cik").GetString() ?? string.Empty,
                m => m.GetProperty("Status").GetString() ?? "unknown",
                StringComparer.Ordinal);

        var wanted = new List<(string Cik, string Name, string LastCohort, string Status)>();

        foreach (var member in manifest.RootElement.GetProperty("Members").EnumerateArray())
        {
            var cik = member.GetProperty("Cik").GetString() ?? string.Empty;
            var cohorts = member.GetProperty("Cohorts").EnumerateArray()
                .Select(c => c.GetString() ?? string.Empty)
                .ToList();

            if (cohorts.Count >= 5 || !status.TryGetValue(cik, out var identityStatus))
            {
                continue;
            }

            wanted.Add((
                cik,
                member.GetProperty("Name").GetString() ?? "(unnamed)",
                cohorts.Count == 0 ? "0000-00-00" : cohorts.Max(StringComparer.Ordinal)!,
                identityStatus));
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

        var candidates = new List<SurvivorshipProbeSelection.Candidate>();

        foreach (var (cik, name, lastCohort, identityStatus) in wanted)
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

                candidates.Add(new SurvivorshipProbeSelection.Candidate(
                    cik, name, lastCohort, identityStatus,
                    filing.Value.Accession, filing.Value.Form, filing.Value.FilingDate,
                    filing.Value.Document));

                break;
            }
        }

        return candidates;
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

        for (var i = 0; i < count; i++)
        {
            if (forms[i].StartsWith("10-K", StringComparison.OrdinalIgnoreCase) &&
                documents[i].Length > 0)
            {
                return (accessions[i], documents[i], forms[i], dates[i]);
            }
        }

        return null;
    }

    private static List<string> Column(JsonElement recent, string name) =>
        recent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : [];

    private static string Url(SurvivorshipProbeSelection.Candidate candidate) =>
        ArchiveBase
        + candidate.Cik.TrimStart('0')
        + "/"
        + candidate.Accession.Replace("-", string.Empty, StringComparison.Ordinal)
        + "/"
        + candidate.Document;

    private static async Task<string> PrefixAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

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

    private static string? Value(string document, string tag)
    {
        foreach (var pattern in new[]
        {
            "name\\s*=\\s*\"(?:dei:)?" + tag + "\"[^>]*>(?<value>.{0,400}?)</",
            "<(?:\\w+:)?" + tag + "[^>]*>(?<value>.{0,400}?)</",
        })
        {
            var match = Regex.Match(
                document, pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromSeconds(10));

            if (!match.Success)
            {
                continue;
            }

            var text = Regex.Replace(
                match.Groups["value"].Value, "<[^>]*>", string.Empty,
                RegexOptions.None, TimeSpan.FromSeconds(5));

            text = text.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase).Trim();

            if (text.Length is > 0 and <= 128)
            {
                return text;
            }
        }

        return null;
    }

    private static bool IsTransport(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or SocketException;

    private static string Short(Exception exception) =>
        exception.Message.Length > 110 ? exception.Message[..110] : exception.Message;

    // ---- reporting -------------------------------------------------------------------------------------

    private static string Selection(
        List<SurvivorshipProbeSelection.Candidate> pool,
        List<SurvivorshipProbeSelection.Candidate> candidates,
        List<SurvivorshipProbeSelection.Candidate> chosen,
        HashSet<string> alreadyProbed,
        Dns dns)
    {
        var report = new StringBuilder();

        report.AppendLine("# Widened survivorship probe, round two - the sample, frozen before reading");
        report.AppendLine();
        report.AppendLine("Written **before the first request**, so the ten cannot be adjusted once");
        report.AppendLine("the answers start arriving.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Transport: {dns.Summary}"));
        report.AppendLine();
        report.AppendLine("## The selection rule - unchanged from round one, minus who was read");
        report.AppendLine();
        report.AppendLine("Candidates are the unpriceable dropouts whose submissions document is");
        report.AppendLine("already archived and names an annual filing, **excluding every CIK named");
        report.AppendLine("in a previous probe report**. They are grouped into strata of (death");
        report.AppendLine("year, identity category), strata ordered by year then category, members");
        report.AppendLine("inside a stratum by CIK, all ordinal; one is taken from each stratum in");
        report.AppendLine("rotation until ten are chosen. No threshold, nothing tunable.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Pool **{pool.Count}**, already read **{alreadyProbed.Count}**, eligible **{candidates.Count}**, strata **{candidates.Select(c => c.Stratum).Distinct(StringComparer.Ordinal).Count()}**, chosen **{chosen.Count}**."));
        report.AppendLine();
        report.AppendLine("| # | CIK | Company | Died | Identity category | Form | Filed |");
        report.AppendLine("| ---: | --- | --- | --- | --- | --- | --- |");

        var index = 0;

        foreach (var c in chosen)
        {
            index++;
            report.AppendLine(Universe.Inv(
                $"| {index} | `{c.Cik}` | {c.Name} | {c.LastCohort} | {c.Status} | {c.Form} | {c.FilingDate} |"));
        }

        return report.ToString();
    }

    private static string Compose(
        List<SurvivorshipProbeSelection.Candidate> pool,
        List<SurvivorshipProbeSelection.Candidate> candidates,
        List<SurvivorshipProbeSelection.Candidate> chosen,
        List<Finding> findings,
        HashSet<string> alreadyProbed,
        Dns dns,
        int attempted,
        int succeeded,
        int failed,
        int recovered,
        int noSecurity,
        int unresolved,
        int transport)
    {
        // The unchanged method: every readable filing is in the denominator, recovered or not.
        var roundReadable = recovered + noSecurity + unresolved;
        var totalRecovered = PriorRecovered + recovered;
        var totalReadable = PriorRecovered + PriorNotRecovered + roundReadable;

        var rate = SurvivorshipProbeSelection.Rate(totalRecovered, totalReadable - totalRecovered);
        var (low, high) = SurvivorshipProbeSelection.Interval(totalRecovered, totalReadable);
        var justified = SurvivorshipProbeSelection.JustifiesFullRecovery(totalRecovered, totalReadable);

        // The second figure, stated and NOT substituted: filings that had a ticker to recover.
        var hadATicker = totalReadable - noSecurity;
        var (adjustedLow, adjustedHigh) = SurvivorshipProbeSelection.Interval(totalRecovered, hadATicker);

        var report = new StringBuilder();

        report.AppendLine("# Survivorship probe - twenty filings");
        report.AppendLine();
        report.AppendLine("**No EODHD call. No price, split, dividend or corporate action. Nothing");
        report.AppendLine("written to the observation store, the manifest or the identity table.**");
        report.AppendLine("A ticker found here is evidence; it is not applied to any member, and the");
        report.AppendLine("full recovery has NOT been started.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Unpriceable dropouts with a readable filing | {pool.Count} |"));
        report.AppendLine(Universe.Inv($"| Already read in earlier rounds | {alreadyProbed.Count} |"));
        report.AppendLine(Universe.Inv($"| Eligible this round | {candidates.Count} |"));
        report.AppendLine(Universe.Inv($"| Selected | {chosen.Count} |"));
        report.AppendLine(Universe.Inv($"| SEC calls attempted | {attempted} of {CallBudget} |"));
        report.AppendLine(Universe.Inv($"| Succeeded / failed | {succeeded} / {failed} |"));
        report.AppendLine(Universe.Inv($"| EODHD calls | 0 |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Transport: {dns.Summary}"));
        report.AppendLine();

        report.AppendLine("## This round, by outcome");
        report.AppendLine();
        report.AppendLine("| Outcome | Filings | In the denominator |");
        report.AppendLine("| --- | ---: | --- |");
        report.AppendLine(Universe.Inv($"| 1. Ticker recovered from the issuer's filing | **{recovered}** | yes |"));
        report.AppendLine(Universe.Inv($"| 2. No listed security - nothing to recover | **{noSecurity}** | yes (see below) |"));
        report.AppendLine(Universe.Inv($"| 3. No ticker, listed status unresolved | **{unresolved}** | yes |"));
        report.AppendLine(Universe.Inv($"| 4. Transport failure - never reached SEC | {transport} | **no** |"));
        report.AppendLine();

        report.AppendLine("## Every filing this round");
        report.AppendLine();
        report.AppendLine("| CIK | Company | Died | Category | Outcome | Symbol | Exchange | Security title | Evidence |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (var f in findings.OrderBy(f => f.Candidate.LastCohort, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv(
                $"| `{f.Candidate.Cik}` | {f.Candidate.Name} | {f.Candidate.LastCohort} | {f.Candidate.Status} | {f.Outcome} | {f.Symbol ?? "-"} | {f.Exchange ?? "-"} | {f.Title ?? "-"} | {f.Evidence} |"));
        }

        report.AppendLine();
        report.AppendLine("## Cumulative, across all twenty");
        report.AppendLine();
        report.AppendLine("| | Round one | Round two | Total |");
        report.AppendLine("| --- | ---: | ---: | ---: |");
        report.AppendLine(Universe.Inv($"| Recovered | {PriorRecovered} | {recovered} | **{totalRecovered}** |"));
        report.AppendLine(Universe.Inv($"| Not recovered | {PriorNotRecovered} | {noSecurity + unresolved} | {totalReadable - totalRecovered} |"));
        report.AppendLine(Universe.Inv($"| Readable filings | {PriorRecovered + PriorNotRecovered} | {roundReadable} | **{totalReadable}** |"));
        report.AppendLine(Universe.Inv($"| Transport failures (excluded) | 0 | {transport} | {transport} |"));
        report.AppendLine();
        report.AppendLine("**Same method as round one, unchanged.** Wilson interval at 95%, every");
        report.AppendLine("readable filing in the denominator, transport failures outside it.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| **Observed recovery rate** | **{rate:P0}** |"));
        report.AppendLine(Universe.Inv($"| 95% interval (Wilson) | {low:P0} to {high:P0} |"));
        report.AppendLine(Universe.Inv($"| Pre-registered bar: lower bound above 50% | **{(justified ? "MET" : "NOT MET")}** |"));
        report.AppendLine();

        report.AppendLine("## The hypothesis: absence of a ticker versus absence of a listing");
        report.AppendLine();
        report.AppendLine("Round one raised it: a company with no listed security has no ticker to");
        report.AppendLine("tag, so its filing is not a failure of the method. Each call this round");
        report.AppendLine("read the Section 12(b) heading from the same document it already had, so");
        report.AppendLine("testing it cost nothing.");
        report.AppendLine();

        if (noSecurity > 0)
        {
            report.AppendLine(Universe.Inv(
                $"**Supported, on {noSecurity} filing(s) this round.** Their cover pages state no securities registered under Section 12(b). There was no ticker to recover, so counting them as method failures understates it."));
            report.AppendLine();
            report.AppendLine("Reported as a second figure, **not substituted for the one above**:");
            report.AppendLine();
            report.AppendLine("| | |");
            report.AppendLine("| --- | ---: |");
            report.AppendLine(Universe.Inv($"| Filings that had a listed security to recover | {hadATicker} |"));
            report.AppendLine(Universe.Inv($"| Rate over those | {SurvivorshipProbeSelection.Rate(totalRecovered, hadATicker - totalRecovered):P0} |"));
            report.AppendLine(Universe.Inv($"| 95% interval | {adjustedLow:P0} to {adjustedHigh:P0} |"));
            report.AppendLine();
            report.AppendLine("The pre-registered bar is judged on the first figure only. This second");
            report.AppendLine("one is offered so the difference between the two questions is visible,");
            report.AppendLine("not as a replacement for the measure the bar was set against.");
        }
        else
        {
            report.AppendLine("**Not supported this round.** No filing here stated that it had no");
            report.AppendLine("Section 12(b) security, so this round produced no evidence for the");
            report.AppendLine("hypothesis either way.");
        }

        report.AppendLine();
        report.AppendLine("**Avon and SPYR themselves remain untested.** Their documents were not");
        report.AppendLine("retained, re-reading them would cost calls this round is not authorised");
        report.AppendLine("to spend, and they are carried forward as not-recovered rather than");
        report.AppendLine("reclassified on a hypothesis. Two free calls would settle them.");
        report.AppendLine();

        report.AppendLine("## What this justifies");
        report.AppendLine();
        report.AppendLine(justified
            ? Universe.Inv($"**The bar is met.** {totalRecovered} of {totalReadable} readable filings state their own symbol, lower bound {low:P0} against a bar of 50%. The evidence now supports a full recovery over the remaining unpriceable dropouts - which would still leave some unmatched, and those must remain members of the sealed universe.")
            : Universe.Inv($"**The bar is not met.** {totalRecovered} of {totalReadable} readable filings state their own symbol, lower bound {low:P0} against a bar of 50%."));
        report.AppendLine();
        report.AppendLine("The full recovery has not been started. The sealed universe is unchanged");
        report.AppendLine("and gate 2's floor is untouched.");

        return report.ToString();
    }

    private enum Outcome
    {
        Recovered,
        NoListedSecurity,
        ListedStatusUnresolved,
        TransportFailure,
    }

    private sealed record Dns(bool Resolved, string Summary);

    private sealed record Finding(
        SurvivorshipProbeSelection.Candidate Candidate,
        Outcome Outcome,
        string? Symbol,
        string? Exchange,
        string? Title,
        string Evidence)
    {
        public static Finding Transport(
            SurvivorshipProbeSelection.Candidate candidate, string reason) =>
            new(candidate, Outcome.TransportFailure, null, null, null,
                "never reached SEC: " + reason);
    }
}
