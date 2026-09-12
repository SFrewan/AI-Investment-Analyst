using System.Text;
using System.Text.Json;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Where archived evidence is kept, and why it is no longer wherever the process was started.
/// </summary>
/// <remarks>
/// <para>
/// The archive root used to be resolved with <c>Path.GetFullPath</c> against the working
/// directory. Under <c>dotnet test</c> that is the test assembly's output folder, so every payload
/// this system has acquired - including four SEC filing documents that cost acquisition
/// authorisation to obtain - was written beneath <c>bin/</c>, which git ignores and a rebuild or
/// workspace clean may remove. The bytes were real evidence in a directory nothing treats as
/// evidence, and no database table holds a second copy of what the sidecars say.
/// </para>
/// <para>
/// These tests hold the two halves of the fix apart: the shipped configuration resolves to the
/// durable archive, and this assembly cannot write into it.
/// </para>
/// <para>
/// <strong>Nothing here contacts a provider.</strong> Every payload stored below is a literal byte
/// array written to a temporary directory that the test deletes.
/// </para>
/// </remarks>
public sealed class RawArchiveRootTests
{
    private const string ConfiguredRootPath = "src/AI.Investment.Api/archive";

    // ---- 1. the working directory can no longer decide where evidence goes -----------------------

    /// <summary>
    /// The resolved root is measured from the repository, not from wherever the process started.
    /// </summary>
    /// <remarks>
    /// The anchor is derived from <c>AppContext.BaseDirectory</c>, which is a property of the
    /// assembly rather than of the launch, so a root measured from it cannot move when the working
    /// directory does. The second assertion makes that concrete for this run: the working directory
    /// here really is the build output, and the old resolution really would have put the archive
    /// back inside it.
    /// </remarks>
    [Fact]
    public void The_working_directory_cannot_redirect_the_archive_into_build_output()
    {
        var resolved = RawArchiveRoot.Resolve(ConfiguredRootPath);

        Assert.Equal(RepositoryRoot(), RawArchiveRoot.Anchor());
        Assert.Equal(DurableArchive(), resolved);
        Assert.True(Path.IsPathFullyQualified(resolved));

        // Only meaningful when the two differ, which under `dotnet test` they do. Stated as a
        // condition rather than an assumption so the test proves something rather than failing on
        // a runner that happens to start in the repository root.
        var workingDirectoryRoot = Path.GetFullPath(ConfiguredRootPath);

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory),
                Path.TrimEndingDirectorySeparator(RepositoryRoot()),
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.NotEqual(workingDirectoryRoot, resolved);
        }

        Assert.DoesNotContain(
            resolved.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 2. the shipped configuration names the durable location ---------------------------------

    [Fact]
    public void The_shipped_configuration_resolves_to_the_durable_archive()
    {
        foreach (var settings in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            var configured = ConfiguredRoot(settings);

            Assert.Equal(ConfiguredRootPath, configured);
            Assert.False(Path.IsPathFullyQualified(configured), $"{settings} must not pin a machine.");
            Assert.Equal(DurableArchive(), RawArchiveRoot.Resolve(configured));
        }

        // The location is real, is outside every build output directory, and already holds the
        // evidence that made it the durable one.
        Assert.True(Directory.Exists(DurableArchive()));
        Assert.NotEmpty(Directory.GetFiles(DurableArchive(), "*.bin", SearchOption.AllDirectories));
    }

    /// <summary>
    /// The API host's own composition resolves to the durable archive.
    /// </summary>
    /// <remarks>
    /// Reads the configuration the real host built rather than a builder of this test's own. A
    /// check that proved some other pipeline resolved the right directory would prove the wrong
    /// thing on the day it mattered. The value is compared against the test override deliberately:
    /// this assertion is what would fail if the isolation in <see cref="TestArchiveRoot"/> were
    /// ever pointed at the evidence store.
    /// </remarks>
    [Fact]
    public void The_booted_host_resolves_the_root_it_was_configured_with()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();

        var configured = scope.ServiceProvider
            .GetRequiredService<IOptions<RawArchiveOptions>>()
            .Value
            .RootPath;

        Assert.Equal(TestArchiveRoot.Location, RawArchiveRoot.Resolve(configured));
        Assert.NotEqual(DurableArchive(), RawArchiveRoot.Resolve(configured));
    }

    /// <summary>
    /// The host the authorised SEC filing-document batch runs under resolves the test root, not the
    /// durable one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SecFilingDocumentBatchTests</c> takes <see cref="UniverseApiFactory"/> as its class
    /// fixture, and <c>SecFilingDocumentBatchDoor.OpenAsync</c> resolves
    /// <c>IRawResponseArchive</c> out of that container - so this is the directory an authorised
    /// document would be written to. Asserted against the real factory rather than reasoned about,
    /// because the whole question of the gate is which of two directories this actually is.
    /// </para>
    /// <para>
    /// A connection string is layered on afterwards so the host can start without one in the
    /// environment. It touches no database - nothing here opens a connection - and it does not go
    /// near <c>RawArchive</c>, which still comes from the factory's own chain.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_authorised_filing_document_host_resolves_the_isolated_test_root()
    {
        using var factory = new UniverseApiFactory();
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] =
                        "Host=localhost;Port=5432;Database=api_tests_unreachable;Username=postgres;Password=postgres",
                    ["Database:CommandTimeoutSeconds"] = "5",
                })));

        using var scope = host.Services.CreateScope();

        var resolved = RawArchiveRoot.Resolve(
            scope.ServiceProvider.GetRequiredService<IOptions<RawArchiveOptions>>().Value.RootPath);

        Assert.Equal(TestArchiveRoot.Location, resolved);
        Assert.NotEqual(DurableArchive(), resolved);
    }

    // ---- 3, 4, 5, 6. the store itself is untouched -----------------------------------------------

    [Fact]
    public async Task The_archive_is_still_content_addressed_with_an_unchanged_sidecar()
    {
        var root = TemporaryRoot();

        try
        {
            var archive = Archive(root);
            var payload = Encoding.UTF8.GetBytes("{\"close\":12.34}");
            var retrieved = new DateTime(2026, 9, 12, 17, 20, 22, DateTimeKind.Utc);

            var hash = await archive.StoreAsync(
                EodhdProvider.Id,
                payload,
                "application/json",
                retrieved);

            // 3. The address is the hash, and the layout is the two-level fan-out.
            Assert.Equal(ContentHash.Compute(payload).Value, hash.Value);

            var expected = Path.Combine(root, hash.Value[..2], hash.Value[2..4], hash.Value + ".bin");

            Assert.True(File.Exists(expected), $"Expected the payload at {expected}.");
            Assert.Equal(payload, await File.ReadAllBytesAsync(expected));

            // 4. The sidecar still holds exactly four fields, and no request detail.
            var sidecar = Path.ChangeExtension(expected, ".json");

            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(sidecar));

            Assert.Equal(
                ["SourceId", "MediaType", "RetrievedAtUtc", "ByteLength"],
                document.RootElement.EnumerateObject().Select(property => property.Name));

            // 5. EODHD provenance round-trips exactly as before.
            var described = await archive.DescribeAsync(hash);

            Assert.NotNull(described);
            Assert.Equal(EodhdProvider.Id, described.SourceId);
            Assert.Equal("application/json", described.MediaType);
            Assert.Equal(retrieved, described.RetrievedAtUtc);
            Assert.Equal(payload.Length, described.ByteLength);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A filing document is archived under the same root as every other payload.
    /// </summary>
    /// <remarks>
    /// There is one root and no per-category branch, which is what makes the retention fix reach
    /// filing documents at all. The category the F6i host fix introduced changed which SEC host a
    /// request goes to; it changed nothing about where the answer is kept.
    /// </remarks>
    [Fact]
    public async Task A_filing_document_is_archived_under_the_same_root_as_a_price_payload()
    {
        var root = TemporaryRoot();

        try
        {
            var archive = Archive(root);
            var retrieved = new DateTime(2026, 9, 12, 17, 20, 22, DateTimeKind.Utc);

            var price = await archive.StoreAsync(
                EodhdProvider.Id,
                Encoding.UTF8.GetBytes("[{\"close\":1}]"),
                "application/json",
                retrieved);

            var document = await archive.StoreAsync(
                SecEdgarProvider.Id,
                Encoding.UTF8.GetBytes("<html><body>8-K</body></html>"),
                "text/html",
                retrieved);

            foreach (var hash in new[] { price, document })
            {
                var path = Path.Combine(root, hash.Value[..2], hash.Value[2..4], hash.Value + ".bin");

                Assert.True(File.Exists(path), $"Expected {hash.Value} beneath the single root.");
            }

            var filing = await archive.DescribeAsync(document);

            Assert.NotNull(filing);
            Assert.Equal(SecEdgarProvider.Id, filing.SourceId);
            Assert.Equal("text/html", filing.MediaType);
        }
        finally
        {
            Delete(root);
        }
    }

    // ---- 7. an absolute root is honoured, and every root is absolute -----------------------------

    /// <summary>
    /// A fully qualified root is used exactly as written.
    /// </summary>
    /// <remarks>
    /// Every tool and harness in this repository passes one. Anchoring a path that is already
    /// anchored would silently relocate them - and, for the harnesses, would relocate them into the
    /// working tree.
    /// </remarks>
    [Fact]
    public void A_fully_qualified_root_is_neither_moved_nor_anchored()
    {
        var absolute = TemporaryRoot();

        Assert.Equal(absolute, RawArchiveRoot.Resolve(absolute));
        Assert.DoesNotContain(RepositoryRoot(), RawArchiveRoot.Resolve(absolute), StringComparison.Ordinal);

        foreach (var candidate in new[] { ConfiguredRootPath, "archive", absolute })
        {
            Assert.True(
                Path.IsPathFullyQualified(RawArchiveRoot.Resolve(candidate)),
                $"'{candidate}' must resolve to an absolute directory.");
        }

        Assert.Throws<ArgumentNullException>(() => RawArchiveRoot.Resolve(null!));
        Assert.Throws<ArgumentException>(() => RawArchiveRoot.Resolve("   "));
    }

    // ---- 8. an identifier cannot address anything outside the root -------------------------------

    /// <summary>
    /// The only thing that names a file in the archive is a content hash, and a content hash
    /// cannot contain a path.
    /// </summary>
    /// <remarks>
    /// Traversal is refused by the identifier type rather than by the store, which is the stronger
    /// place for it: every caller reaches the filesystem through <see cref="ContentHash"/>, so
    /// there is no second path to audit.
    /// </remarks>
    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("/absolute/elsewhere")]
    [InlineData("C:\\Windows")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000/../x")]
    public void An_artifact_identifier_cannot_traverse_out_of_the_root(string candidate)
    {
        Assert.False(ContentHash.TryCreate(candidate, out _));
        Assert.ThrowsAny<Exception>(() => ContentHash.Create(candidate));
    }

    [Fact]
    public async Task A_valid_identifier_always_addresses_a_file_beneath_the_root()
    {
        var root = TemporaryRoot();

        try
        {
            var archive = Archive(root);

            var hash = await archive.StoreAsync(
                SecEdgarProvider.Id,
                Encoding.UTF8.GetBytes("<html/>"),
                "text/html",
                DateTime.UtcNow);

            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                Assert.StartsWith(root, Path.GetFullPath(file), StringComparison.Ordinal);
            }

            Assert.NotNull(await archive.RetrieveAsync(hash));
        }
        finally
        {
            Delete(root);
        }
    }

    // ---- 9. no provider or source identity moved -------------------------------------------------

    /// <summary>
    /// Retention is about where bytes are kept. It says nothing about who produced them.
    /// </summary>
    [Fact]
    public void No_provider_identity_changed()
    {
        Assert.Equal("eodhd-eod", EodhdProvider.Id.Value);
        Assert.Equal("eodhd-splits", EodhdSplitsProvider.Id.Value);
        Assert.Equal("sec-edgar", SecEdgarProvider.Id.Value);
        Assert.Equal("operator-price-history", PriceHistoryFileProvider.Id.Value);
    }

    // ---- the isolation itself --------------------------------------------------------------------

    /// <summary>
    /// This assembly writes to its own output directory and cannot reach the durable archive.
    /// </summary>
    /// <remarks>
    /// The override is explicit and absolute. That matters as much as where it points: the defect
    /// being fixed was not that the archive was in the wrong place, it was that nothing said where
    /// the archive was, so the answer came from whatever directory the process happened to start
    /// in.
    /// </remarks>
    [Fact]
    public void The_test_host_is_isolated_from_the_durable_archive()
    {
        var configured = Environment.GetEnvironmentVariable(TestArchiveRoot.ConfigurationVariable);

        Assert.NotNull(configured);
        Assert.Equal(TestArchiveRoot.Location, configured);
        Assert.True(Path.IsPathFullyQualified(configured));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "archive"), configured);
        Assert.NotEqual(DurableArchive(), RawArchiveRoot.Resolve(configured!));

        // The isolation must not be the old bare relative value under another name.
        Assert.NotEqual("archive", configured);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>Reads <c>RawArchive:RootPath</c> out of a shipped settings file.</summary>
    /// <remarks>
    /// Reads the file in the repository rather than the copy in this assembly's output, so the
    /// assertion is about what the repository ships and not about what a build happened to copy.
    /// </remarks>
    private static string ConfiguredRoot(string settingsFile)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(
                Path.Combine(RepositoryRoot(), "src", "AI.Investment.Api", settingsFile)));

        return document.RootElement
            .GetProperty(RawArchiveOptions.SectionName)
            .GetProperty(nameof(RawArchiveOptions.RootPath))
            .GetString()!;
    }

    private static FileSystemRawResponseArchive Archive(string root) =>
        new(Options.Create(new RawArchiveOptions { RootPath = root }));

    private static string DurableArchive() =>
        Path.GetFullPath(Path.Combine(RepositoryRoot(), "src", "AI.Investment.Api", "archive"));

    /// <summary>
    /// The repository root, derived the way the rest of this project derives it, and deliberately
    /// not by re-running the resolver's own search.
    /// </summary>
    private static string RepositoryRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string TemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), "f6m-" + Guid.NewGuid().ToString("n"));

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
