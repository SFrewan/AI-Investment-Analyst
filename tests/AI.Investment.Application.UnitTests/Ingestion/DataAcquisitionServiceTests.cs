using System.Text;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Ingestion;

/// <summary>
/// Joining the fetch to the interpretation, and knowing when not to.
/// </summary>
/// <remarks>
/// The service is deliberately thin, so these tests are almost entirely about restraint: what it
/// declines to do, and what it declines to claim. A refused run must not be normalised, because
/// that would turn a compliance decision into a fabricated data-quality problem; and "we did not
/// try" must stay distinguishable from "we tried and found nothing".
/// </remarks>
public sealed class DataAcquisitionServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId TestSource = SourceId.Create("test-source");

    private static IngestionRequest Request() =>
        IngestionRequest.Create(
            TestSource,
            DataCategory.CompanyProfile,
            Region.UnitedStates,
            IngestionSubject.Create("Company", "0000320193"),
            CorrelationId.New(),
            Now);

    /// <summary>A gateway that returns a run built to order.</summary>
    private sealed class StubIngestionGateway : IIngestionGateway
    {
        private readonly Func<IngestionRequest, IngestionRun> _build;

        public StubIngestionGateway(Func<IngestionRequest, IngestionRun> build) => _build = build;

        public int Calls { get; private set; }

        public Task<IngestionRun> IngestAsync(
            IngestionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(_build(request));
        }
    }

    private sealed class StubNormalizationPipeline : INormalizationPipeline
    {
        private readonly NormalizationSummary _summary;

        public StubNormalizationPipeline(NormalizationSummary? summary = null) =>
            _summary = summary ?? new NormalizationSummary(1, 3, 0);

        public int Calls { get; private set; }

        public IngestionRun? LastRun { get; private set; }

        public Task<NormalizationSummary> NormalizeAsync(
            IngestionRun run,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRun = run;

            return Task.FromResult(_summary);
        }
    }

    /// <summary>A completed run that archived the given number of payloads.</summary>
    private static IngestionRun SucceededWith(IngestionRequest request, int artifacts)
    {
        var run = IngestionRun.Start(request, Now);

        for (var i = 0; i < artifacts; i++)
        {
            run.RecordArtifact(ContentHash.Compute(Encoding.UTF8.GetBytes($"payload {i}")));
        }

        run.MarkSucceeded(Now);

        return run;
    }

    /// <summary>A completed run that archived one payload.</summary>
    /// <remarks>
    /// A separate method rather than an optional parameter on <see cref="SucceededWith"/>, because
    /// this one is passed as a method group to <c>Func&lt;IngestionRequest, IngestionRun&gt;</c>.
    /// A method group converts to a delegate only when its parameter list matches exactly -
    /// optional values are applied at an explicit call, never during the conversion - so a
    /// two-parameter method cannot become a one-parameter delegate however it is defaulted.
    /// A separate name rather than an overload, for the same reason the mappers use explicit
    /// lambdas: an overloaded method group is one more thing for inference to get wrong.
    /// </remarks>
    private static IngestionRun Succeeded(IngestionRequest request) =>
        SucceededWith(request, artifacts: 1);

    private static IngestionRun Refused(IngestionRequest request) =>
        IngestionRun.Refuse(request, "ingestion.source-registered@1", "not registered", Now);

    // ---------- the chain ----------

    [Fact]
    public async Task A_successful_run_is_normalised()
    {
        var gateway = new StubIngestionGateway(Succeeded);
        var pipeline = new StubNormalizationPipeline();

        var result = await new DataAcquisitionService(gateway, pipeline).AcquireAsync(Request());

        Assert.Equal(1, gateway.Calls);
        Assert.Equal(1, pipeline.Calls);
        Assert.NotNull(result.Normalization);
        Assert.Equal(3, result.ObservationsRecorded);
        Assert.True(result.WasFetched);
    }

    [Fact]
    public async Task The_pipeline_is_given_the_run_the_gateway_returned()
    {
        var gateway = new StubIngestionGateway(Succeeded);
        var pipeline = new StubNormalizationPipeline();

        var result = await new DataAcquisitionService(gateway, pipeline).AcquireAsync(Request());

        // The same run object, not a reconstruction. Its artifacts are what normalisation reads,
        // and rebuilding it from the request would lose them.
        Assert.Same(result.Run, pipeline.LastRun);
    }

    [Fact]
    public async Task A_partial_run_is_still_normalised()
    {
        var gateway = new StubIngestionGateway(request =>
        {
            var run = IngestionRun.Start(request, Now);
            run.RecordArtifact(ContentHash.Compute("page one"u8));
            run.MarkPartiallySucceeded("the page limit was reached", Now);

            return run;
        });

        var pipeline = new StubNormalizationPipeline();

        var result = await new DataAcquisitionService(gateway, pipeline).AcquireAsync(Request());

        // It archived real bytes; that more was expected is recorded on the run. Discarding what
        // did arrive would throw away good data over an incomplete fetch.
        Assert.Equal(1, pipeline.Calls);
        Assert.True(result.WasFetched);
        Assert.Equal(IngestionOutcome.PartiallySucceeded, result.Run.Outcome);
    }

    // ---------- restraint ----------

    [Fact]
    public async Task A_refused_run_is_not_normalised()
    {
        var gateway = new StubIngestionGateway(Refused);
        var pipeline = new StubNormalizationPipeline();

        var result = await new DataAcquisitionService(gateway, pipeline).AcquireAsync(Request());

        // Normalising it would quarantine a payload that was never fetched, inventing a
        // data-quality problem out of a compliance decision.
        Assert.Equal(0, pipeline.Calls);
        Assert.False(result.WasFetched);
    }

    [Fact]
    public async Task A_run_that_was_not_normalised_reports_null_rather_than_zero()
    {
        var gateway = new StubIngestionGateway(Refused);

        var result = await new DataAcquisitionService(gateway, new StubNormalizationPipeline())
            .AcquireAsync(Request());

        // A zero-filled summary would say "we looked and found nothing". Null says "we did not
        // look", which is what happened.
        Assert.Null(result.Normalization);
        Assert.Equal(0, result.ObservationsRecorded);
    }

    [Fact]
    public async Task A_successful_run_that_archived_nothing_is_not_normalised()
    {
        var gateway = new StubIngestionGateway(request => SucceededWith(request, artifacts: 0));
        var pipeline = new StubNormalizationPipeline();

        var result = await new DataAcquisitionService(gateway, pipeline).AcquireAsync(Request());

        Assert.Equal(0, pipeline.Calls);
        Assert.Null(result.Normalization);

        // The fetch itself did succeed. Reporting otherwise would blame the source for returning
        // nothing when it may simply have had nothing to return.
        Assert.True(result.WasFetched);
    }

    [Fact]
    public async Task A_denied_write_leaves_the_run_and_the_archive_intact()
    {
        var gateway = new StubIngestionGateway(Succeeded);

        // Payloads read, none recorded - what the pipeline reports when policy refused the write.
        var pipeline = new StubNormalizationPipeline(new NormalizationSummary(1, 0, 0));

        var result = await new DataAcquisitionService(gateway, pipeline).AcquireAsync(Request());

        // The fetch is not undone by a refusal to record what it meant. The bytes are archived and
        // the run is in the ledger, so a later replay costs nobody's rate limit.
        Assert.True(result.WasFetched);
        Assert.Equal(0, result.ObservationsRecorded);
        Assert.Equal(1, result.Normalization!.PayloadsRead);
    }

    // ---------- refusals ----------

    [Fact]
    public async Task A_null_request_is_refused()
    {
        var service = new DataAcquisitionService(
            new StubIngestionGateway(Succeeded),
            new StubNormalizationPipeline());

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.AcquireAsync(null!));
    }

    [Fact]
    public void Null_collaborators_are_refused()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DataAcquisitionService(null!, new StubNormalizationPipeline()));

        Assert.Throws<ArgumentNullException>(
            () => new DataAcquisitionService(new StubIngestionGateway(Succeeded), null!));
    }
}
