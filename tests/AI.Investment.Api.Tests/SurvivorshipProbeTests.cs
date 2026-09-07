using System.Globalization;
using System.Net;
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
/// Ten free SEC requests, to find out how often a dead company's own filing names its ticker.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why widen rather than decide.</strong> Two probes read four filings between them and
/// found the tag in two. Half of four settles nothing: the interval around it spans almost the
/// whole range, and a hundred-and-ten-company recovery cannot be authorised or refused on that.
/// Ten more free requests buy a sample that can at least narrow it.
/// </para>
/// <para>
/// <strong>The sample is frozen before anything is read.</strong> The selection rule is arithmetic
/// over facts already on disk, and the chosen ten are written into the report before the first
/// request goes out - so the sample cannot be adjusted once the answers start arriving, and a
/// reader can check that it was not.
/// </para>
/// <para>
/// <strong>Four outcomes, kept apart.</strong> Recovered, proven absent, inconclusive, and
/// transport failure. The last is the one that matters most for honesty: a request that never
/// reached SEC measured the network, not the filing, and it is excluded from the rate rather than
/// counted as an absence. Two such failures in the earlier probes would otherwise have halved the
/// apparent recovery rate for a reason that has nothing to do with filings.
/// </para>
/// </remarks>
public sealed class SurvivorshipProbeTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_SURVIVORSHIP_PROBE";

    /// <summary>The hard ceiling, enforced by the transport rather than by care.</summary>
    private const int CallBudget = 10;

    /// <summary>How much of each filing to read. The cover page is at the front.</summary>
    private const int PrefixBytes = 8 * 1024 * 1024;

    private const string Host = "www.sec.gov";

    private const string ArchiveBase = "https://" + Host + "/Archives/edgar/data/";

    private static readonly string[] Tags =
    [
        "TradingSymbol",
        "Security12bTitle",
        "SecurityExchangeName",
    ];

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SurvivorshipProbeTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Ten_dead_companies_are_asked_whether_their_own_filings_name_them()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The widened probe is off. Set {GateVariable}=1 to run it. It makes at most " +
            $"{CallBudget} free SEC requests and no billable call.");

        var contact = Environment.GetEnvironmentVariable("AIINV_SEC_CONTACT");

        Skip.If(
            string.IsNullOrWhiteSpace(contact),
            "AIINV_SEC_CONTACT is not set and EDGAR fair access requires a contact address.");

        // ---- step 1: the transport, diagnosed for nothing ---------------------------------------

        var dns = await ResolveAsync();

        _output.WriteLine(dns.Summary);

        // ---- step 2: the sample, frozen and written down before anything is read ----------------

        var candidates = await CandidatesAsync(_factory.Services);

        Skip.If(
            candidates.Count == 0,
            "No archived submissions document names an annual filing for any unpriceable dropout.");

        var chosen = SurvivorshipProbeSelection.Choose(candidates);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "survivorship-probe-selection.md"),
            Selection(candidates, chosen, dns));

        Assert.True(chosen.Count <= CallBudget);
        Assert.Equal(
            chosen.Count,
            chosen.Select(c => c.Cik).Distinct(StringComparer.Ordinal).Count());

        Skip.If(
            !dns.Resolved,
            "The SEC archive host does not resolve from this machine right now, so every request "
            + "would fail before reaching SEC and would tell us nothing about any filing. The "
            + "frozen selection has been written; re-run when the host resolves. " + dns.Summary);

        // ---- step 3: the calls, ceiling enforced by the transport --------------------------------

        using var budget = new BoundedCalls(CallBudget)
        {
            InnerHandler = new SocketsHttpHandler
            {
                // One pooled connection for the whole run: the host is resolved once above, and
                // re-resolving per request is what made the earlier probes fail intermittently.
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
                var prefix = await PrefixAsync(client, Url(candidate));

                succeeded++;
                findings.Add(Read(candidate, prefix));
            }
            catch (Exception exception) when (IsTransport(exception))
            {
                failed++;
                findings.Add(Finding.Transport(candidate, Short(exception)));
            }

            // EDGAR fair access, and there are only ten.
            await Task.Delay(300);
        }

        // ---- step 4: what it establishes, and what it does not -----------------------------------

        var recovered = findings.Count(f => f.Outcome == Outcome.Recovered);
        var absent = findings.Count(f => f.Outcome == Outcome.ProvenAbsent);
        var inconclusive = findings.Count(f => f.Outcome == Outcome.Inconclusive);
        var transport = findings.Count(f => f.Outcome == Outcome.TransportFailure);

        var report = Compose(
            candidates, chosen, findings, dns,
            attempted, succeeded, failed,
            recovered, absent, inconclusive, transport);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "survivorship-probe-extended.md"),
            report);

        _output.WriteLine(report);

        Assert.True(attempted <= CallBudget, $"{attempted} calls against a ceiling of {CallBudget}.");
        Assert.Equal(attempted, succeeded + failed);
        Assert.Equal(chosen.Count, findings.Count);

        // The four outcomes account for every filing, with none double-counted.
        Assert.Equal(findings.Count, recovered + absent + inconclusive + transport);

        // Transport failures are outside the denominator. This is the assertion that stops a bad
        // network being read as evidence about filings.
        Assert.Equal(failed, transport);
        Assert.Equal(
            SurvivorshipProbeSelection.Rate(recovered, absent),
            recovered + absent == 0 ? 0m : (decimal)recovered / (recovered + absent));
    }

    // ---- the transport, diagnosed without spending a call ------------------------------------------

    /// <summary>
    /// Resolves the archive host, with retries. A DNS lookup is not an HTTP request to SEC.
    /// </summary>
    /// <remarks>
    /// Both earlier probes lost a call to <c>No such host is known</c> while neighbouring requests
    /// to the same host succeeded seconds later, which is a resolver problem rather than anything
    /// about EDGAR. Resolving first costs nothing against the authorised budget, turns an
    /// intermittent failure into a wait, and - when the host genuinely will not resolve - lets the
    /// stage decline to spend ten calls that could only fail.
    /// </remarks>
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
                    return new Dns(
                        true,
                        attempt,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{Host} resolved to {addresses.Length} address(es) on attempt {attempt} of 3. No SEC request was spent doing it."));
                }

                attempts.Add(string.Create(
                    CultureInfo.InvariantCulture, $"attempt {attempt}: resolved to no addresses"));
            }
            catch (SocketException exception)
            {
                attempts.Add(string.Create(
                    CultureInfo.InvariantCulture, $"attempt {attempt}: {exception.SocketErrorCode}"));
            }

            await Task.Delay(2000);
        }

        return new Dns(
            false,
            3,
            Host + " did not resolve in 3 attempts (" + string.Join("; ", attempts)
            + "). No SEC request was spent establishing that.");
    }

    // ---- who to ask -------------------------------------------------------------------------------

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

        // The 110: unpriceable AND a dropout. Both facts predate this stage.
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

    // ---- reading ------------------------------------------------------------------------------------

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

    private static Finding Read(SurvivorshipProbeSelection.Candidate candidate, string document)
    {
        var tagged = Tags.Where(t =>
            document.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();

        if (tagged.Count == 0)
        {
            // The filer's own document does not carry the tag. That is an answer, not a gap.
            return new Finding(
                candidate,
                Outcome.ProvenAbsent,
                null,
                null,
                null,
                Universe.Inv(
                    $"none of the three cover-page tags appears in the first {document.Length} characters of `{candidate.Document}`"));
        }

        var symbol = Value(document, "TradingSymbol");
        var exchange = Value(document, "SecurityExchangeName");
        var title = Value(document, "Security12bTitle");

        if (symbol is null)
        {
            // Tagged, but the value could not be read. Neither a recovery nor an absence, and
            // saying so is the whole reason this outcome exists.
            return new Finding(
                candidate,
                Outcome.Inconclusive,
                null,
                exchange,
                title,
                Universe.Inv(
                    $"{string.Join(", ", tagged)} present but no trading symbol could be extracted"));
        }

        return new Finding(
            candidate,
            Outcome.Recovered,
            symbol,
            exchange,
            title,
            Universe.Inv($"issuer-stated on the cover page of a {candidate.Form} filed {candidate.FilingDate}"));
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
                document,
                pattern,
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

    // ---- reporting ------------------------------------------------------------------------------------

    private static string Selection(
        List<SurvivorshipProbeSelection.Candidate> candidates,
        List<SurvivorshipProbeSelection.Candidate> chosen,
        Dns dns)
    {
        var report = new StringBuilder();

        report.AppendLine("# Widened survivorship probe - the sample, frozen before reading");
        report.AppendLine();
        report.AppendLine("Written **before the first request**, so the ten cannot be adjusted once");
        report.AppendLine("the answers start arriving and a reader can check that they were not.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Transport: {dns.Summary}"));
        report.AppendLine();
        report.AppendLine(Rule(candidates, chosen));

        return report.ToString();
    }

    private static string Rule(
        List<SurvivorshipProbeSelection.Candidate> candidates,
        List<SurvivorshipProbeSelection.Candidate> chosen)
    {
        var report = new StringBuilder();

        report.AppendLine("## The selection rule");
        report.AppendLine();
        report.AppendLine("Candidates are the unpriceable dropouts whose submissions document is");
        report.AppendLine("already archived and names an annual filing - no call is spent finding");
        report.AppendLine("them. They are grouped into strata of (death year, identity category),");
        report.AppendLine("strata ordered by year then category, members inside a stratum by CIK,");
        report.AppendLine("all ordinal. One member is taken from each stratum in rotation until ten");
        report.AppendLine("are chosen. The rule contains no threshold and nothing that could be");
        report.AppendLine("tuned after seeing a filing.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Candidate pool: **{candidates.Count}**. Strata: **{candidates.Select(c => c.Stratum).Distinct(StringComparer.Ordinal).Count()}**. Chosen: **{chosen.Count}**."));
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
        List<SurvivorshipProbeSelection.Candidate> candidates,
        List<SurvivorshipProbeSelection.Candidate> chosen,
        List<Finding> findings,
        Dns dns,
        int attempted,
        int succeeded,
        int failed,
        int recovered,
        int absent,
        int inconclusive,
        int transport)
    {
        var readable = recovered + absent;
        var rate = SurvivorshipProbeSelection.Rate(recovered, absent);
        var (low, high) = SurvivorshipProbeSelection.Interval(recovered, readable);
        var justified = SurvivorshipProbeSelection.JustifiesFullRecovery(recovered, readable);

        var report = new StringBuilder();

        report.AppendLine("# Widened survivorship probe - ten filings, asked properly");
        report.AppendLine();
        report.AppendLine("**No EODHD call. No price, split, dividend or corporate action. Nothing");
        report.AppendLine("written to the observation store, the manifest or the identity table.**");
        report.AppendLine("A ticker found here is evidence; it is not applied to any member.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Candidate pool | {candidates.Count} |"));
        report.AppendLine(Universe.Inv($"| Selected | {chosen.Count} |"));
        report.AppendLine(Universe.Inv($"| SEC calls attempted | {attempted} of {CallBudget} |"));
        report.AppendLine(Universe.Inv($"| Succeeded / failed | {succeeded} / {failed} |"));
        report.AppendLine(Universe.Inv($"| EODHD calls | 0 |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Transport: {dns.Summary}"));
        report.AppendLine();

        report.AppendLine("## The four outcomes, kept apart");
        report.AppendLine();
        report.AppendLine("| Outcome | Filings | Counts toward the rate |");
        report.AppendLine("| --- | ---: | --- |");
        report.AppendLine(Universe.Inv($"| **Recovered** - the filing states its own symbol | **{recovered}** | yes |"));
        report.AppendLine(Universe.Inv($"| **Proven absent** - the tag is not in the document | **{absent}** | yes |"));
        report.AppendLine(Universe.Inv($"| Inconclusive - tagged, value unreadable | {inconclusive} | no |"));
        report.AppendLine(Universe.Inv($"| Transport failure - never reached SEC | {transport} | **no** |"));
        report.AppendLine();
        report.AppendLine("A request that never reached SEC measured the network, not the filing.");
        report.AppendLine("Counting it as an absence would move the rate for a reason that has");
        report.AppendLine("nothing to do with whether filers tag their cover pages.");
        report.AppendLine();

        report.AppendLine("## Every filing");
        report.AppendLine();
        report.AppendLine("| CIK | Company | Died | Category | Outcome | Symbol | Exchange | Security title | Evidence |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (var f in findings.OrderBy(f => f.Candidate.LastCohort, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv(
                $"| `{f.Candidate.Cik}` | {f.Candidate.Name} | {f.Candidate.LastCohort} | {f.Candidate.Status} | {f.Outcome} | {f.Symbol ?? "-"} | {f.Exchange ?? "-"} | {f.Title ?? "-"} | {f.Evidence} |"));
        }

        report.AppendLine();
        report.AppendLine("## The rate, with what it is worth");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Readable filings (the denominator) | {readable} |"));
        report.AppendLine(Universe.Inv($"| Recovered | {recovered} |"));
        report.AppendLine(Universe.Inv($"| **Observed recovery rate** | **{rate:P0}** |"));
        report.AppendLine(Universe.Inv($"| 95% interval (Wilson) | {low:P0} to {high:P0} |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"**The interval is the finding, not the percentage.** {readable} filings is a small sample, and the range above is how much it leaves open. Any projection onto the {candidates.Count} unpriceable dropouts inherits that whole range, and the members that could not be read are not a random subset of them - they are the ones whose documents this probe could reach, which is itself a selection."));
        report.AppendLine();

        report.AppendLine("## Is a full recovery justified?");
        report.AppendLine();
        report.AppendLine("The bar was fixed before any filing was read: at least 8 readable filings,");
        report.AppendLine("and the lower bound of the interval above one half. The lower bound rather");
        report.AppendLine("than the estimate, because it is the lower bound that says what the");
        report.AppendLine("evidence supports.");
        report.AppendLine();
        report.AppendLine(justified
            ? Universe.Inv($"**Met.** {recovered} of {readable} readable filings state their own symbol, lower bound {low:P0}. On this evidence a full recovery over the remaining unpriceable dropouts is worth authorising - it would still leave some unmatched, and those must stay members.")
            : Universe.Inv($"**Not met.** {recovered} of {readable} readable filings state their own symbol, lower bound {low:P0}, against a bar of 50%. The evidence does not support committing {candidates.Count * 2} free calls to a recovery whose yield would leave the enlarged panel's survivorship properties unmeasured."));
        report.AppendLine();

        report.AppendLine("## Recommendation");
        report.AppendLine();
        report.AppendLine(justified
            ? "**B** - recover identities from filings, then re-judge the panel's survivorship before pricing anything. The recovery is a means to a measurable panel, not an end: if the recovered set still fails gate 2, the answer is still not to price it."
            : "**Not A, and not C as a research panel.** The recoverable fraction is too uncertain to enlarge the panel meaningfully, and pricing the 269 alone still yields a 98.1% survivor panel that fails gate 2 at 1.86%. That leaves **C as infrastructure validation only, with no research claim attached**, or **D - stop until the research question is restated to one this evidence base can answer**. Both are decisions about what is being asked, not about data, and neither is taken here.");
        report.AppendLine();
        report.AppendLine("The sealed universe is unchanged and gate 2's floor is untouched.");

        return report.ToString();
    }

    private enum Outcome
    {
        Recovered,
        ProvenAbsent,
        Inconclusive,
        TransportFailure,
    }

    private sealed record Dns(bool Resolved, int Attempts, string Summary);

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
