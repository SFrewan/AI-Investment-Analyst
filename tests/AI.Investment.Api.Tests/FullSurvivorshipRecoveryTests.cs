using System.Diagnostics;
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
/// The full historical identity recovery: every unpriceable member, asked of its own filings.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is for.</strong> EDGAR strips a company's tickers when it deregisters, so the
/// 131 members that cannot be priced are very nearly the ones that died - and pricing only the 269
/// that remain would produce a 98.1% survivor panel while the manifest still truthfully read
/// 28.8%. A company's own filings do not disappear, and twenty probe filings established that
/// their cover pages name the ticker often enough for this to be worth doing.
/// </para>
/// <para>
/// <strong>The expected outcome is not "everything recovered".</strong> It is a truthful
/// partition: recovered, no listed security, unresolved, transport-failed. A company that cannot
/// be identified stays a member of the sealed universe and is reported. Nothing is dropped,
/// nothing is fuzzy-matched, no vendor name match is promoted, and no identity is ever inferred
/// from a price series.
/// </para>
/// <para>
/// <strong>Every member is re-read, including the twenty already probed.</strong> That spends
/// about twenty free calls the probes already spent - and buys a single artefact in which all 131
/// members were classified by one code path, at one moment, under one rule. Stitching four reports
/// with three different classification schemes into a recovery table would have been cheaper and
/// much easier to get quietly wrong.
/// </para>
/// <para>
/// <strong>The sealed universe is not touched.</strong> Recovery writes a new artefact beside the
/// identity table; it does not edit the manifest, the identity table, membership, ordering or any
/// threshold. No price, split, dividend or corporate action is fetched, and no EODHD call is
/// possible from this stage.
/// </para>
/// </remarks>
public sealed class FullSurvivorshipRecoveryTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_FULL_RECOVERY";

    /// <summary>The authorised ceiling, enforced by the transport rather than by care.</summary>
    private const int CallBudget = 214;

    /// <summary>Same read size as the probe that proved the mechanism.</summary>
    private const int PrefixBytes = 8 * 1024 * 1024;

    private const string Host = "www.sec.gov";

    private const string ArchiveBase = "https://" + Host + "/Archives/edgar/data/";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public FullSurvivorshipRecoveryTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Every_unpriceable_member_is_asked_of_its_own_filings_and_none_is_dropped()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The full recovery is off. Set {GateVariable}=1 to run it. It makes at most " +
            $"{CallBudget} free SEC requests and no billable call.");

        var contact = Environment.GetEnvironmentVariable("AIINV_SEC_CONTACT");

        Skip.If(
            string.IsNullOrWhiteSpace(contact),
            "AIINV_SEC_CONTACT is not set and EDGAR fair access requires a contact address.");

        var watch = Stopwatch.StartNew();

        // ---- the seal, before anything ------------------------------------------------------------

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var manifestBefore = await File.ReadAllBytesAsync(manifestPath);

        Assert.True(
            IdentityResolution.IsSealedAs(
                Encoding.UTF8.GetString(manifestBefore), SealedFingerprint),
            "The manifest on disk is not the sealed universe this recovery was authorised against.");

        var dns = await ResolveAsync();

        // ---- who to ask: every member that cannot be priced today ---------------------------------

        var members = await MembersAsync(_factory.Services);

        Assert.Equal(400, members.Count);

        var unpriceable = members.Where(m => !m.Ready).ToList();

        Skip.If(unpriceable.Count == 0, "Every member is already acquisition-ready.");

        Skip.If(
            !dns.Resolved,
            "The SEC archive host does not resolve from this machine right now, so every request "
            + "would fail before reaching SEC. Nothing was spent. " + dns.Summary);

        // ---- the calls ------------------------------------------------------------------------------

        using var budget = new BoundedCalls(CallBudget)
        {
            InnerHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(20),
                ConnectTimeout = TimeSpan.FromSeconds(30),
            },
        };

        using var client = new HttpClient(budget) { Timeout = TimeSpan.FromMinutes(2) };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "AI-Investment-Analyst/1.0 (" + contact + ")");

        var results = new List<Result>(unpriceable.Count);
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;
        var notRequested = 0;

        foreach (var member in unpriceable)
        {
            // No archived filing to read: no call is spent, and the member is unresolved rather
            // than counted as an absence of evidence it was never asked for.
            if (member.Accession is null || member.Document is null)
            {
                notRequested++;

                results.Add(new Result(
                    member,
                    RecoveryPartition.Unresolved,
                    null, null, null,
                    "no archived annual filing names a document to read, and no call was spent"));

                continue;
            }

            if (attempted >= CallBudget)
            {
                notRequested++;

                results.Add(new Result(
                    member,
                    RecoveryPartition.Unresolved,
                    null, null, null,
                    "not requested: the authorised call ceiling was reached"));

                continue;
            }

            attempted++;

            try
            {
                var document = await PrefixAsync(client, Url(member));

                succeeded++;
                results.Add(Read(member, document));
            }
            catch (Exception exception) when (IsTransport(exception))
            {
                failed++;

                results.Add(new Result(
                    member,
                    RecoveryPartition.TransportFailed,
                    null, null, null,
                    "never reached SEC: " + Short(exception)));
            }

            await Task.Delay(250);
        }

        // ---- the partition ----------------------------------------------------------------------------

        var recovered = results.Count(r => r.Outcome == RecoveryPartition.Recovered);
        var noSecurity = results.Count(r => r.Outcome == RecoveryPartition.NoListedSecurity);
        var unresolved = results.Count(r => r.Outcome == RecoveryPartition.Unresolved);
        var transport = results.Count(r => r.Outcome == RecoveryPartition.TransportFailed);

        Assert.True(
            RecoveryPartition.Covers(unpriceable.Count, recovered, noSecurity, unresolved, transport),
            Universe.Inv($"{unpriceable.Count} members did not partition into {recovered}/{noSecurity}/{unresolved}/{transport}."));

        // No ticker may be invented: every recovered row carries a symbol, every other row does not.
        Assert.All(
            results.Where(r => r.Outcome == RecoveryPartition.Recovered),
            r => Assert.False(string.IsNullOrWhiteSpace(r.Symbol)));
        Assert.All(
            results.Where(r => r.Outcome != RecoveryPartition.Recovered),
            r => Assert.Null(r.Symbol));

        // ---- the artefacts ------------------------------------------------------------------------------

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-recovery.json"),
            JsonSerializer.Serialize(
                new RecoveryArtefact(
                    SealedFingerprint,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    members.Count,
                    members.Count(m => m.Ready),
                    recovered,
                    noSecurity,
                    unresolved,
                    transport,
                    results.Select(r => new RecoveredMember(
                        r.Member.Cik,
                        r.Member.Name,
                        r.Outcome,
                        r.Symbol,
                        r.Exchange,
                        r.Title,
                        r.Member.Status,
                        r.Member.LastCohort,
                        r.Member.IsDropout,
                        r.Evidence)).ToList()),
                Universe.Json) + "\n");

        var report = Compose(
            members, unpriceable, results, dns,
            attempted, succeeded, failed, notRequested,
            recovered, noSecurity, unresolved, transport, watch);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "full-survivorship-recovery.md"),
            report);

        _output.WriteLine(report);

        // ---- nothing moved that must not ------------------------------------------------------------------

        var manifestAfter = await File.ReadAllBytesAsync(manifestPath);

        Assert.Equal(manifestBefore.Length, manifestAfter.Length);
        Assert.True(manifestBefore.SequenceEqual(manifestAfter));

        // Every member is still a member, in the same order, with the same cohorts.
        var after = await MembersAsync(_factory.Services);

        Assert.Equal(400, after.Count);
        Assert.Equal(
            members.Select(m => m.Cik).ToList(),
            after.Select(m => m.Cik).ToList());
        Assert.Equal(
            members.Select(m => m.LastCohort).ToList(),
            after.Select(m => m.LastCohort).ToList());

        Assert.True(attempted <= CallBudget, $"{attempted} calls against a ceiling of {CallBudget}.");
        Assert.Equal(attempted, succeeded + failed);
    }

    // ---- classification -------------------------------------------------------------------------------

    private static Result Read(Member member, string document)
    {
        var symbol = Value(document, "TradingSymbol");

        if (symbol is not null)
        {
            return new Result(
                member,
                RecoveryPartition.Recovered,
                symbol,
                Value(document, "SecurityExchangeName"),
                Value(document, "Security12bTitle"),
                Universe.Inv(
                    $"issuer-stated on the cover page of a {member.Form} filed {member.FilingDate}"));
        }

        return RegisteredUnder12b(document) switch
        {
            false => new Result(
                member,
                RecoveryPartition.NoListedSecurity,
                null, null, null,
                Universe.Inv(
                    $"the cover page states no securities registered under Section 12(b); there was no ticker to recover ({document.Length} characters read)")),

            _ => new Result(
                member,
                RecoveryPartition.Unresolved,
                null, null, null,
                Universe.Inv(
                    $"no trading symbol in {document.Length} characters of `{member.Document}`, and the cover page does not settle whether a Section 12(b) security existed")),
        };
    }

    private static bool? RegisteredUnder12b(string document)
    {
        var text = Regex.Replace(
            document, "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(30));

        text = text.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, "\\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(30));

        var heading = Regex.Match(
            text,
            "Securities\\s+registered\\s+pursuant\\s+to\\s+Section\\s+12\\s*\\(\\s*b\\s*\\)",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(30));

        if (!heading.Success)
        {
            return null;
        }

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

        return Regex.IsMatch(
            window,
            "Title\\s+of|Trading\\s+Symbol|Name\\s+of\\s+each\\s+exchange|New\\s+York\\s+Stock|Nasdaq|NYSE",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(10))
                ? true
                : null;
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

    // ---- reading the membership -------------------------------------------------------------------------

    private static async Task<List<Member>> MembersAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json")));

        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        var identities = identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .ToDictionary(
                m => m.GetProperty("Cik").GetString() ?? string.Empty,
                m => (
                    Ready: m.GetProperty("Ready").GetBoolean(),
                    Status: m.GetProperty("Status").GetString() ?? "unknown"),
                StringComparer.Ordinal);

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

        var members = new List<Member>();

        foreach (var entry in manifest.RootElement.GetProperty("Members").EnumerateArray())
        {
            var cik = entry.GetProperty("Cik").GetString() ?? string.Empty;
            var cohorts = entry.GetProperty("Cohorts").EnumerateArray()
                .Select(c => c.GetString() ?? string.Empty)
                .ToList();

            identities.TryGetValue(cik, out var status);

            string? accession = null;
            string? document = null;
            string form = "-";
            string filed = "-";

            if (!status.Ready && latest.TryGetValue(cik, out var run))
            {
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

                    accession = filing.Value.Accession;
                    document = filing.Value.Document;
                    form = filing.Value.Form;
                    filed = filing.Value.FilingDate;

                    break;
                }
            }

            members.Add(new Member(
                cik,
                entry.GetProperty("Name").GetString() ?? "(unnamed)",
                status.Ready,
                status.Status ?? "unknown",
                cohorts.Count == 0 ? "0000-00-00" : cohorts.Max(StringComparer.Ordinal)!,
                cohorts.Count < 5,
                accession,
                document,
                form,
                filed));
        }

        return members;
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

    private static string Url(Member member) =>
        ArchiveBase
        + member.Cik.TrimStart('0')
        + "/"
        + (member.Accession ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal)
        + "/"
        + member.Document;

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

    private static bool IsTransport(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or SocketException;

    private static string Short(Exception exception) =>
        exception.Message.Length > 110 ? exception.Message[..110] : exception.Message;

    // ---- reporting --------------------------------------------------------------------------------------

    private static string Compose(
        List<Member> members,
        List<Member> unpriceable,
        List<Result> results,
        Dns dns,
        int attempted,
        int succeeded,
        int failed,
        int notRequested,
        int recovered,
        int noSecurity,
        int unresolved,
        int transport,
        Stopwatch watch)
    {
        var baselineReady = members.Count(m => m.Ready);
        var baselineDropouts = members.Count(m => m.Ready && m.IsDropout);

        var recoveredMembers = results
            .Where(r => r.Outcome == RecoveryPartition.Recovered)
            .ToList();

        var panelReady = baselineReady + recovered;
        var panelDropouts = baselineDropouts + recoveredMembers.Count(r => r.Member.IsDropout);

        var universeDropouts = members.Count(m => m.IsDropout);

        var report = new StringBuilder();

        report.AppendLine("# Full historical identity recovery");
        report.AppendLine();
        report.AppendLine("**No EODHD call. No price, split, dividend or corporate action. No");
        report.AppendLine("opportunity, score or order.** The sealed manifest is byte-identical, all");
        report.AppendLine("400 members remain members in the same order, and no threshold moved.");
        report.AppendLine("**Price acquisition has NOT been started.**");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Sealed universe `{SealedFingerprint}`, verified before and after."));
        report.AppendLine();

        report.AppendLine("## Calls");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Members that could not be priced | {unpriceable.Count} |"));
        report.AppendLine(Universe.Inv($"| **SEC calls attempted** | **{attempted}** of {CallBudget} |"));
        report.AppendLine(Universe.Inv($"| Succeeded | {succeeded} |"));
        report.AppendLine(Universe.Inv($"| Failed in transport | {failed} |"));
        report.AppendLine(Universe.Inv($"| Not requested (no archived filing, or ceiling) | {notRequested} |"));
        report.AppendLine(Universe.Inv($"| Refused at the gateway | 0 |"));
        report.AppendLine(Universe.Inv($"| **EODHD calls** | **0** |"));
        report.AppendLine(Universe.Inv($"| Price / split / dividend calls | 0 / 0 / 0 |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Transport: {dns.Summary}"));
        report.AppendLine();

        report.AppendLine("## The partition");
        report.AppendLine();
        report.AppendLine("| Outcome | Members | Acquirable |");
        report.AppendLine("| --- | ---: | --- |");
        report.AppendLine(Universe.Inv($"| **Recovered** - the issuer's filing names a ticker | **{recovered}** | yes |"));
        report.AppendLine(Universe.Inv($"| **No listed security** - nothing to recover | **{noSecurity}** | no |"));
        report.AppendLine(Universe.Inv($"| **Unresolved** - evidence insufficient | **{unresolved}** | no |"));
        report.AppendLine(Universe.Inv($"| **Transport failed** - never reached SEC | **{transport}** | no |"));
        report.AppendLine(Universe.Inv($"| Total | {unpriceable.Count} | |"));
        report.AppendLine();
        report.AppendLine("Every one of these remains a member of the sealed universe. A company that");
        report.AppendLine("could not be identified is reported, not dropped.");
        report.AppendLine();

        report.AppendLine(Universe.Inv($"## Recovered: {recovered} CIK to ticker mappings"));
        report.AppendLine();
        report.AppendLine("| CIK | Company | Ticker | Exchange | Security title | Died | Was |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");

        foreach (var r in recoveredMembers.OrderBy(r => r.Member.Cik, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv(
                $"| `{r.Member.Cik}` | {r.Member.Name} | **{r.Symbol}** | {r.Exchange ?? "-"} | {r.Title ?? "-"} | {(r.Member.IsDropout ? r.Member.LastCohort : "survived")} | {r.Member.Status} |"));
        }

        report.AppendLine();
        Section(report, results, RecoveryPartition.NoListedSecurity,
            "No listed security - there was no ticker to recover");
        Section(report, results, RecoveryPartition.Unresolved,
            "Unresolved - evidence genuinely insufficient");
        Section(report, results, RecoveryPartition.TransportFailed,
            "Transport failed - never reached SEC, and NOT evidence of absence");

        report.AppendLine("## What it does to the panel");
        report.AppendLine();
        report.AppendLine("| | Members | Dropouts | Dropout share | Gate 2 (floor 5%) |");
        report.AppendLine("| --- | ---: | ---: | ---: | --- |");
        report.AppendLine(Universe.Inv(
            $"| Baseline acquisition-ready | {baselineReady} | {baselineDropouts} | {RecoveryPartition.PanelDropoutShare(baselineReady, baselineDropouts):P1} | {(RecoveryPartition.ClearsGate2(baselineReady, baselineDropouts) ? "PASS" : "**FAIL**")} |"));
        report.AppendLine(Universe.Inv(
            $"| **After recovery** | **{panelReady}** | **{panelDropouts}** | **{RecoveryPartition.PanelDropoutShare(panelReady, panelDropouts):P1}** | {(RecoveryPartition.ClearsGate2(panelReady, panelDropouts) ? "**PASS**" : "**FAIL**")} |"));
        report.AppendLine(Universe.Inv(
            $"| Sealed universe, for reference | {members.Count} | {universeDropouts} | {RecoveryPartition.PanelDropoutShare(members.Count, universeDropouts):P1} | PASS |"));
        report.AppendLine();

        report.AppendLine("### Survivorship implications");
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"The panel that could be priced before this recovery held {baselineDropouts} companies that stopped filing out of {baselineReady} - {RecoveryPartition.PanelDropoutShare(baselineReady, baselineDropouts):P1}, against a sealed universe of {RecoveryPartition.PanelDropoutShare(members.Count, universeDropouts):P1}. Pricing it would have measured survivors while the manifest truthfully said otherwise."));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"After recovery the panel holds {panelDropouts} of {panelReady} - {RecoveryPartition.PanelDropoutShare(panelReady, panelDropouts):P1}. That is {(RecoveryPartition.ClearsGate2(panelReady, panelDropouts) ? "above" : "below")} gate 2's unchanged 5% floor, and it is the figure a later acquisition would be judged on."));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"**{unpriceable.Count - recovered} members still cannot be priced.** They remain members of the sealed four hundred and are listed above with the reason. The panel is smaller than the universe and always will be; what matters is that it is no longer a panel of survivors, and that the gap is stated rather than hidden."));
        report.AppendLine();
        report.AppendLine("The pre-registered reliability bar was not met and has not been changed.");
        report.AppendLine("This recovery was authorised on its survivorship remediation impact, which");
        report.AppendLine("is a different question, and no claim is made that the old bar passed.");
        report.AppendLine();

        report.AppendLine("## Budget");
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"{attempted} of {CallBudget} authorised free SEC calls spent; **{CallBudget - attempted} remain**. The separate reserved calls - 1 identity and 1 refinement - were not touched. Zero EODHD, zero price, zero split, zero dividend."));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalMinutes:F1} minutes."));

        return report.ToString();
    }

    private static void Section(
        StringBuilder report, List<Result> results, string outcome, string heading)
    {
        var rows = results.Where(r => r.Outcome == outcome).ToList();

        report.AppendLine(Universe.Inv($"## {heading}: {rows.Count}"));
        report.AppendLine();

        if (rows.Count == 0)
        {
            report.AppendLine("None.");
            report.AppendLine();

            return;
        }

        report.AppendLine("| CIK | Company | Died | Was | Evidence |");
        report.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var r in rows.OrderBy(r => r.Member.Cik, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv(
                $"| `{r.Member.Cik}` | {r.Member.Name} | {(r.Member.IsDropout ? r.Member.LastCohort : "survived")} | {r.Member.Status} | {r.Evidence} |"));
        }

        report.AppendLine();
    }

    private sealed record Dns(bool Resolved, string Summary);

    private sealed record Member(
        string Cik,
        string Name,
        bool Ready,
        string Status,
        string LastCohort,
        bool IsDropout,
        string? Accession,
        string? Document,
        string Form,
        string FilingDate);

    private sealed record Result(
        Member Member,
        string Outcome,
        string? Symbol,
        string? Exchange,
        string? Title,
        string Evidence);

    private sealed record RecoveredMember(
        string Cik,
        string Name,
        string Outcome,
        string? Ticker,
        string? Exchange,
        string? SecurityTitle,
        string PreviousStatus,
        string LastCohort,
        bool IsDropout,
        string Evidence);

    private sealed record RecoveryArtefact(
        string EvidenceBaseFingerprint,
        string CompletedAtUtc,
        int Members,
        int BaselineReady,
        int Recovered,
        int NoListedSecurity,
        int Unresolved,
        int TransportFailed,
        List<RecoveredMember> Results);
}
