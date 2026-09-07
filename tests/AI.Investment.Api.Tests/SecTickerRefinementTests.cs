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
/// Four free SEC requests, to settle whether two filings really lack a cover-page ticker.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the first probe left open.</strong> Five filings were read; two stated their own
/// trading symbol cleanly, one never reached SEC at all, and two showed no tag in the first two
/// megabytes. That last result has two very different explanations - the tag is genuinely absent,
/// or the probe looked in the wrong place - and they lead to opposite decisions about whether a
/// hundred-and-ten-company recovery is worth authorising. Reporting "2 of 5" as though it measured
/// the method would have been a guess dressed as a finding.
/// </para>
/// <para>
/// <strong>What this does differently.</strong> Two calls per filing rather than one. The first
/// reads the filing's own index, which names every file in the accession; the second fetches the
/// document that index says is the XBRL instance, and reads all of it rather than a prefix. If the
/// tag is absent from the instance the filer named, it is absent.
/// </para>
/// <para>
/// <strong>Still free, still bounded, still evidence only.</strong> Four SEC requests, ceiling
/// enforced by the transport. No EODHD call, no price, no split, no dividend. Nothing is written
/// to the observation store, the manifest or the identity table.
/// </para>
/// </remarks>
public sealed class SecTickerRefinementTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_SEC_TICKER_REFINEMENT";

    /// <summary>Two filings, two calls each.</summary>
    private const int CallBudget = 4;

    /// <summary>The whole instance, not a prefix. Capped only against a runaway response.</summary>
    private const int MaxBytes = 48 * 1024 * 1024;

    private const string ArchiveBase = "https://www.sec.gov/Archives/edgar/data/";

    /// <summary>
    /// The two CIKs the first probe could not settle. The accession is READ FROM THE ARCHIVE, not
    /// stated here: an accession number written into a test by hand is a number nobody checked,
    /// and pointing four authorised calls at a guessed URL would waste them proving nothing.
    /// </summary>
    private static readonly string[] Unsettled =
    [
        "0000829325",
        "0001063537",
    ];

    private static readonly string[] Tags =
    [
        "TradingSymbol",
        "Security12bTitle",
        "SecurityExchangeName",
    ];

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SecTickerRefinementTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_two_inconclusive_filings_are_read_properly_and_settled()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The probe refinement is off. Set {GateVariable}=1 to run it. It makes at most " +
            $"{CallBudget} free SEC requests and no billable call.");

        var contact = Environment.GetEnvironmentVariable("AIINV_SEC_CONTACT");

        Skip.If(
            string.IsNullOrWhiteSpace(contact),
            "AIINV_SEC_CONTACT is not set and EDGAR fair access requires a contact address.");

        var targets = await TargetsAsync(_factory.Services);

        Skip.If(
            targets.Count == 0,
            "No archived submissions document names an annual filing for either unsettled CIK, so " +
            "there is no accession to read and no call worth spending.");

        using var budget = new BoundedCalls(CallBudget)
        {
            InnerHandler = new HttpClientHandler(),
        };

        using var client = new HttpClient(budget)
        {
            Timeout = TimeSpan.FromMinutes(2),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "AI-Investment-Analyst/1.0 (" + contact + ")");

        var findings = new List<Finding>();
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;

        foreach (var target in targets)
        {
            // ---- call one: what files does this accession actually contain? --------------------
            string? instance = null;
            var files = new List<string>();
            string? failure = null;

            attempted++;

            try
            {
                var index = await TextAsync(client, Folder(target) + "index.json");

                succeeded++;
                files = Files(index);
                instance = ChooseInstance(files, target);
            }
            catch (Exception exception) when (IsTransport(exception))
            {
                failed++;
                failure = "index: " + Short(exception);
            }

            await Task.Delay(300);

            if (failure is not null || instance is null)
            {
                findings.Add(new Finding(
                    target,
                    null,
                    null,
                    null,
                    failure ?? Universe.Inv(
                        $"the accession lists {files.Count} files and none of them is an XBRL instance this probe can read"),
                    files.Count));

                continue;
            }

            // ---- call two: the instance itself, read whole ---------------------------------------
            attempted++;

            try
            {
                var document = await TextAsync(client, Folder(target) + instance);

                succeeded++;
                findings.Add(Read(target, instance, document, files.Count));
            }
            catch (Exception exception) when (IsTransport(exception))
            {
                failed++;

                findings.Add(new Finding(
                    target, instance, null, null, "instance: " + Short(exception), files.Count));
            }

            await Task.Delay(300);
        }

        var settled = findings.Count(f => f.Symbol is not null);
        var absent = findings.Count(f => f.Symbol is null && f.Failure is null);

        var report = Compose(findings, attempted, succeeded, failed, settled, absent);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "sec-ticker-refinement.md"),
            report);

        _output.WriteLine(report);

        Assert.True(attempted <= CallBudget, $"{attempted} calls against a ceiling of {CallBudget}.");
        Assert.Equal(attempted, succeeded + failed);
        Assert.Equal(targets.Count, findings.Count);

        // Every filing ends either settled or explained. Neither outcome is a failure of this
        // test: it exists to replace a guess with evidence, and "genuinely absent" is evidence.
        Assert.All(findings, f => Assert.True(
            f.Symbol is not null || f.Evidence is not null || f.Failure is not null));
    }

    // ---- where the accessions come from -----------------------------------------------------------

    /// <summary>
    /// The latest annual filing each unsettled CIK actually has, read from the archived submissions.
    /// </summary>
    /// <remarks>
    /// Zero calls: the submissions documents were fetched during the identity pass and are still in
    /// the archive. Deriving the accession here rather than writing it into the file is the whole
    /// difference between checking a filing and checking a URL somebody typed.
    /// </remarks>
    private static async Task<List<Target>> TargetsAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

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

        var targets = new List<Target>();

        foreach (var cik in Unsettled)
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

                targets.Add(new Target(
                    cik,
                    Name(payload) ?? "(unnamed)",
                    filing.Value.Accession,
                    filing.Value.Form,
                    filing.Value.FilingDate));

                break;
            }
        }

        return targets;
    }

    private static string? Name(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);

        return document.RootElement.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
    }

    private static (string Accession, string Form, string FilingDate)? LatestAnnualFiling(
        byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);

        if (!document.RootElement.TryGetProperty("filings", out var filings) ||
            !filings.TryGetProperty("recent", out var recent))
        {
            return null;
        }

        var forms = Column(recent, "form");
        var accessions = Column(recent, "accessionNumber");
        var dates = Column(recent, "filingDate");

        var count = new[] { forms.Count, accessions.Count, dates.Count }.Min();

        for (var i = 0; i < count; i++)
        {
            if (forms[i].StartsWith("10-K", StringComparison.OrdinalIgnoreCase))
            {
                return (accessions[i], forms[i], dates[i]);
            }
        }

        return null;
    }

    private static List<string> Column(JsonElement recent, string name) =>
        recent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : [];

    // ---- reading ---------------------------------------------------------------------------------

    private static string Folder(Target target) =>
        ArchiveBase
        + target.Cik.TrimStart('0')
        + "/"
        + target.Accession.Replace("-", string.Empty, StringComparison.Ordinal)
        + "/";

    private static async Task<string> TextAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();

        var buffer = new byte[MaxBytes];
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

    private static List<string> Files(string index)
    {
        using var document = JsonDocument.Parse(index);

        if (!document.RootElement.TryGetProperty("directory", out var directory) ||
            !directory.TryGetProperty("item", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items
            .EnumerateArray()
            .Select(i => i.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty)
            .Where(n => n.Length > 0)
            .ToList();
    }

    /// <summary>
    /// The file most likely to carry the cover-page facts.
    /// </summary>
    /// <remarks>
    /// An extracted instance (<c>*_htm.xml</c>) first, because it is small and contains only the
    /// tagged facts. Otherwise the inline document itself, which for an iXBRL filing is where the
    /// tags live. Exhibits and graphics are never chosen.
    /// </remarks>
    private static string? ChooseInstance(List<string> files, Target target)
    {
        var extracted = files.FirstOrDefault(f =>
            f.EndsWith("_htm.xml", StringComparison.OrdinalIgnoreCase));

        if (extracted is not null)
        {
            return extracted;
        }

        // The primary document: the largest .htm that is not an exhibit and not the index.
        return files
            .Where(f => f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains("index", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Regex.IsMatch(f, "ex[-_]?[0-9]", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
            .OrderBy(f => f.Length)
            .FirstOrDefault();
    }

    private static Finding Read(Target target, string instance, string document, int fileCount)
    {
        var found = new List<string>();
        string? symbol = null;
        string? exchange = null;

        foreach (var tag in Tags)
        {
            if (!document.Contains(tag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Value(document, tag);

            found.Add(value is null ? tag + " (tagged, value not extractable)" : tag + " = " + value);

            if (value is null)
            {
                continue;
            }

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
            ? Universe.Inv(
                $"none of the three cover-page tags appears anywhere in `{instance}` ({document.Length} characters read in full)")
            : string.Join("; ", found);

        return new Finding(target, instance, symbol, exchange, null, fileCount) with
        {
            Evidence = evidence,
        };
    }

    /// <summary>
    /// The value of a tagged fact, whether it sits directly inside the element or inside a span.
    /// </summary>
    /// <remarks>
    /// The first probe required the value to be the element's immediate text, which real inline
    /// XBRL often does not do - it wraps the value in formatting markup. Any tags in the captured
    /// span are stripped, which is why a nested value is now readable.
    /// </remarks>
    private static string? Value(string document, string tag)
    {
        var match = Regex.Match(
            document,
            "name\\s*=\\s*\"(?:dei:)?" + tag + "\"[^>]*>(?<value>.{0,400}?)</",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(10));

        if (!match.Success)
        {
            // A plain XBRL instance writes <dei:TradingSymbol contextRef="...">UFAB</dei:TradingSymbol>
            match = Regex.Match(
                document,
                "<(?:\\w+:)?" + tag + "[^>]*>(?<value>.{0,400}?)</",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromSeconds(10));
        }

        if (!match.Success)
        {
            return null;
        }

        var text = Regex.Replace(
            match.Groups["value"].Value,
            "<[^>]*>",
            string.Empty,
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        text = text.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase).Trim();

        return text.Length is 0 or > 128 ? null : text;
    }

    private static bool IsTransport(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or JsonException;

    private static string Short(Exception exception) =>
        exception.Message.Length > 120 ? exception.Message[..120] : exception.Message;

    // ---- reporting -------------------------------------------------------------------------------

    private static string Compose(
        List<Finding> findings,
        int attempted,
        int succeeded,
        int failed,
        int settled,
        int absent)
    {
        var report = new StringBuilder();

        report.AppendLine("# Historical ticker probe - refinement");
        report.AppendLine();
        report.AppendLine("**No EODHD call. No price, split or dividend. Nothing written to the");
        report.AppendLine("observation store, the manifest or the identity table.** Two filings, two");
        report.AppendLine("calls each: the accession's own index, then the XBRL instance it names,");
        report.AppendLine("read in full rather than as a prefix.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| SEC calls attempted | {attempted} of {CallBudget} |"));
        report.AppendLine(Universe.Inv($"| Succeeded / failed | {succeeded} / {failed} |"));
        report.AppendLine(Universe.Inv($"| **Now stating a trading symbol** | **{settled} of {findings.Count}** |"));
        report.AppendLine(Universe.Inv($"| Tag genuinely absent | {absent} |"));
        report.AppendLine(Universe.Inv($"| EODHD calls | 0 |"));
        report.AppendLine();

        report.AppendLine("## What the instance said");
        report.AppendLine();
        report.AppendLine("| CIK | Company | Form | Files | Instance read | Symbol | Exchange | Evidence |");
        report.AppendLine("| --- | --- | --- | ---: | --- | --- | --- | --- |");

        foreach (var f in findings)
        {
            report.AppendLine(Universe.Inv(
                $"| `{f.Target.Cik}` | {f.Target.Name} | {f.Target.Form} | {f.FileCount} | {f.Instance ?? "-"} | {f.Symbol ?? "-"} | {f.Exchange ?? "-"} | {f.Failure ?? f.Evidence ?? "-"} |"));
        }

        report.AppendLine();
        report.AppendLine("## What this settles");
        report.AppendLine();

        if (settled == findings.Count)
        {
            report.AppendLine("Both filings state their own trading symbol once the real instance is");
            report.AppendLine("read. The first probe's two blanks were its own limitation, not");
            report.AppendLine("absent evidence - so on the filings examined so far the method has");
            report.AppendLine("recovered every symbol it was given a readable document for.");
        }
        else if (settled == 0)
        {
            report.AppendLine("Neither filing states a trading symbol even in the instance it names.");
            report.AppendLine("On this evidence the blanks were real: the method does not recover");
            report.AppendLine("identity for filings of this kind, and a full attempt would leave");
            report.AppendLine("them unmatched however many calls it spent.");
        }
        else
        {
            report.AppendLine("One settles and one does not, which is the genuinely partial answer:");
            report.AppendLine("the method works where a filing tagged its cover page and cannot work");
            report.AppendLine("where a filing did not.");
        }

        report.AppendLine();
        report.AppendLine("Estimated recoverability for the remaining unpriceable members is stated");
        report.AppendLine("in the stage report rather than here, because it depends on this result");
        report.AppendLine("together with the first probe's, and neither is a sample worth");
        report.AppendLine("extrapolating from on its own.");

        return report.ToString();
    }

    private sealed record Target(
        string Cik,
        string Name,
        string Accession,
        string Form,
        string FilingDate);

    private sealed record Finding(
        Target Target,
        string? Instance,
        string? Symbol,
        string? Exchange,
        string? Failure,
        int FileCount)
    {
        public string? Evidence { get; init; }
    }
}
