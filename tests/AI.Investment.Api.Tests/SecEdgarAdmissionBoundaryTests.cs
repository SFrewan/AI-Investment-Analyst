using AI.Investment.Domain.Enums;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The admission boundary: what opening it would do, and what it would still not do.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only and offline.</strong> Nothing here opens admission, touches the registry or
/// reaches a provider. The union is computed the way <c>ReconcileSourceCoverageHandler</c> computes
/// it - <c>source.Categories.Union(shipped.Categories)</c> ordered by ordinal - over sources built in
/// memory, so the mechanism is tested without performing it.
/// </para>
/// <para>
/// <strong>Several of these test a declaration that does not exist yet, on purpose.</strong> F6d
/// added <c>RegulatoryFilingDocuments</c> to <see cref="SecEdgarSource"/> ahead of opening admission
/// and F6e took it back out, because a fresh installation would otherwise have registered the source
/// with the category already admitted. So the future declaration is supplied explicitly here rather
/// than read from the constant: what is under test is the widening rule, not today's value.
/// </para>
/// </remarks>
public sealed class SecEdgarAdmissionBoundaryTests
{
    private const string Evidence = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private static readonly DateTime Now = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>What the registry row holds today, read from production on 2026-09-12.</summary>
    private static readonly DataCategory[] CurrentlyAdmitted =
    [
        DataCategory.RegulatoryFilings,
        DataCategory.CompanyProfile,
        DataCategory.FinancialStatements,
        DataCategory.EarningsDisclosure,
        DataCategory.MarketWideDisclosure,
    ];

    /// <summary>What the definition would declare once the F6e correction is reapplied.</summary>
    private static readonly DataCategory[] WouldDeclare =
    [
        .. CurrentlyAdmitted,
        DataCategory.RegulatoryFilingDocuments,
    ];

    // ---- 1, 2. before admission ------------------------------------------------------------------

    /// <summary>The shipped definition now declares the category, as of F6g.</summary>
    [Fact]
    public void The_definition_declares_filing_documents()
    {
        var definition = new SecEdgarSource().Definition(Now);

        Assert.Equal(WouldDeclare, definition.Categories);
    }

    /// <summary>
    /// A source WITHOUT the category refuses a filing-document request before the wire.
    /// </summary>
    /// <remarks>
    /// <strong>Asserted against a modelled row rather than the shipped definition, because F6g
    /// opened admission.</strong> The property under test never changed and is the one that kept
    /// dispatch closed for seven stages: <c>SourceAdmission</c> is consulted before any connector is
    /// reached, so a source that does not admit the category refuses with no HTTP call and no unit
    /// spent. Modelling the row keeps that provable now that the real one admits it.
    /// </remarks>
    [Fact]
    public void A_source_without_the_category_refuses_a_filing_document_request()
    {
        var source = Active(SourceWith(CurrentlyAdmitted));

        var admission = SourceAdmission.Evaluate(
            source, DataCategory.RegulatoryFilingDocuments, Region.UnitedStates);

        Assert.False(admission.IsAdmitted);
        Assert.NotNull(admission.RuleId);

        // And the category that IS admitted still is, so this is about the category and not the source.
        Assert.True(SourceAdmission
            .Evaluate(source, DataCategory.RegulatoryFilings, Region.UnitedStates)
            .IsAdmitted);
    }

    /// <summary>
    /// 2. A valid, unexpired, exactly-scoped authorisation cannot cause dispatch before admission.
    /// </summary>
    /// <remarks>
    /// <strong>This is the ordering the whole stage turns on.</strong> The authorisation is real -
    /// F6c's, loaded and digest-verified, naming all thirty documents and in date. Admission still
    /// refuses, because an authorisation says which documents may be fetched and admission says
    /// whether this installation takes that category at all. Neither substitutes for the other.
    /// </remarks>
    [Fact]
    public void Before_admission_even_a_valid_authorisation_cannot_cause_dispatch()
    {
        var authorization = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", SecEdgarFilingDocumentPartition.Declaration),
            Evidence,
            Universe.RepositoryPath());

        var target = SecEdgarFilingDocumentPartition.TargetsFor(1)[0];

        // The authorisation covers it, unambiguously.
        Assert.True(authorization
            .Covers(
                SecFilingDocumentBatchRunner.Source, target,
                SecFilingDocumentBatchRunner.WindowFrom, SecFilingDocumentBatchRunner.WindowTo,
                nameof(DataCategory.RegulatoryFilingDocuments), FilingDocumentSubject.SubjectKind)
            .Allowed);

        Assert.True(authorization.IsValidAt(Now).Allowed);
        Assert.Equal(0, authorization.Consumed);

        // And a source that does not admit the category refuses anyway.
        var admission = SourceAdmission.Evaluate(
            Active(SourceWith(CurrentlyAdmitted)),
            DataCategory.RegulatoryFilingDocuments,
            Region.UnitedStates);

        Assert.False(admission.IsAdmitted);

        // Nothing was spent learning that.
        Assert.Equal(0, authorization.Consumed);
        Assert.Equal(30, authorization.Remaining);
    }

    // ---- 3, 15, 20. what opening admission would add ---------------------------------------------

    /// <summary>3. The union adds exactly one category, and it is the filing-document one.</summary>
    [Fact]
    public void Opening_admission_would_add_only_the_filing_document_category()
    {
        var union = Union(CurrentlyAdmitted, WouldDeclare);
        var added = union.Except(CurrentlyAdmitted).ToList();

        Assert.Equal([DataCategory.RegulatoryFilingDocuments], added);
        Assert.Equal(6, union.Count);

        // Every category the row already held survives. Widening cannot drop one.
        foreach (var category in CurrentlyAdmitted)
        {
            Assert.Contains(category, union);
        }
    }

    /// <summary>20. The union cannot reach a category no definition declares.</summary>
    /// <remarks>
    /// <strong>This is what stops admission widening silently.</strong> The handler unions the row
    /// with the shipped definition and nothing else, so a category can only be admitted by a build
    /// that declares it - which is a source change, reviewable in a diff. There is no input by which
    /// a caller could name an extra one.
    /// </remarks>
    [Theory]
    [InlineData(DataCategory.MarketPrices)]
    [InlineData(DataCategory.CorporateActions)]
    [InlineData(DataCategory.News)]
    public void The_union_cannot_admit_a_category_no_definition_declares(DataCategory outsider)
    {
        var union = Union(CurrentlyAdmitted, WouldDeclare);

        Assert.DoesNotContain(outsider, union);

        // Not even when the row somehow already held it would the *definition* be the cause: the
        // union is the row plus the declaration, so an outsider can only arrive from the row.
        var widened = Union([.. CurrentlyAdmitted, outsider], WouldDeclare);

        Assert.Contains(outsider, widened);
        Assert.Equal(
            [DataCategory.RegulatoryFilingDocuments],
            widened.Except([.. CurrentlyAdmitted, outsider]).ToList());
    }

    /// <summary>15, 16. Reconciling one source cannot touch another, and EODHD is unchanged.</summary>
    /// <remarks>
    /// The handler takes a single <c>SourceId</c> and unions that row against that connector's
    /// definition. There is no path by which reconciling <c>sec-edgar</c> reads, proposes or writes
    /// anything about a price or corporate-actions source. Asserted from the definitions themselves,
    /// which are what a reconciliation would compare against.
    /// </remarks>
    [Fact]
    public void No_unrelated_source_or_category_is_affected()
    {
        var prices = new EodhdSource(EodhdOptions()).Definition(Now).Categories;
        var actions = new EodhdSplitsSource(EodhdOptions()).Definition(Now).Categories;

        Assert.Equal([DataCategory.MarketPrices], prices);
        Assert.Equal([DataCategory.CorporateActions], actions);

        // Neither EODHD definition mentions the filing-document category at all.
        Assert.DoesNotContain(DataCategory.RegulatoryFilingDocuments, prices);
        Assert.DoesNotContain(DataCategory.RegulatoryFilingDocuments, actions);

        // And the SEC definition claims neither of theirs.
        var sec = new SecEdgarSource().Definition(Now).Categories;

        Assert.DoesNotContain(DataCategory.MarketPrices, sec);
        Assert.DoesNotContain(DataCategory.CorporateActions, sec);
    }

    /// <summary>17. The submissions acquisition path is untouched by any of this.</summary>
    /// <remarks>
    /// 1,210 successful production requests have been made under <c>RegulatoryFilings</c>. Nothing
    /// in the filing-document work may disturb it, and the two runners name different categories and
    /// different subject kinds so that they cannot be confused for one another.
    /// </remarks>
    [Fact]
    public void The_existing_sec_submissions_acquisition_is_unchanged()
    {
        Assert.Contains(DataCategory.RegulatoryFilings, new SecEdgarSource().Definition(Now).Categories);

        Assert.Equal("Company", SecFilingsBatchRunner.SubjectKind);
        Assert.Equal(FilingDocumentSubject.SubjectKind, SecEdgarFilingDocumentPartition.SubjectKind);
        Assert.NotEqual(SecFilingsBatchRunner.SubjectKind, SecEdgarFilingDocumentPartition.SubjectKind);

        // The two partitions name different authorisation files, so a batch cannot be charged
        // against the other one's budget.
        Assert.NotEqual(
            SecEdgarSixMemberPartition.Declaration,
            SecEdgarFilingDocumentPartition.Declaration);
    }

    // ---- 4, 5. admission is not authorization -----------------------------------------------------

    /// <summary>4. An admitted category authorises no document whatsoever.</summary>
    /// <remarks>
    /// Admission is category-grained; it cannot express "these thirty documents" and does not try
    /// to. With the category admitted, the authorisation still refuses every target it does not
    /// name - which is what keeps the scope thirty documents rather than every 8-K EDGAR holds.
    /// </remarks>
    [Theory]
    [InlineData("0000892482|0001493152-23-999999|form8-k.htm")]
    [InlineData("0000320193|0000320193-24-000001|aapl-20240101.htm")]
    [InlineData("0000892482|0001493152-23-003970|form10-k.htm")]
    [InlineData("*")]
    public void Admission_authorises_no_document_by_itself(string target)
    {
        // The source admits the category - the state admission would be in after opening.
        var admitted = Active(SourceWith(WouldDeclare));

        Assert.True(SourceAdmission
            .Evaluate(admitted, DataCategory.RegulatoryFilingDocuments, Region.UnitedStates)
            .IsAdmitted);

        // And the authorisation refuses the target anyway.
        var authorization = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", SecEdgarFilingDocumentPartition.Declaration),
            Evidence,
            Universe.RepositoryPath());

        var verdict = authorization.Covers(
            SecFilingDocumentBatchRunner.Source, target,
            SecFilingDocumentBatchRunner.WindowFrom, SecFilingDocumentBatchRunner.WindowTo);

        Assert.False(verdict.Allowed);
        Assert.Equal(0, authorization.Consumed);
    }

    /// <summary>5. After admission, the authorisation is still the thing that decides.</summary>
    [Fact]
    public void After_admission_the_authorisation_is_still_required()
    {
        var admitted = Active(SourceWith(WouldDeclare));

        Assert.True(SourceAdmission
            .Evaluate(admitted, DataCategory.RegulatoryFilingDocuments, Region.UnitedStates)
            .IsAdmitted);

        var authorization = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", SecEdgarFilingDocumentPartition.Declaration),
            Evidence,
            Universe.RepositoryPath());

        // Thirty named documents, and nothing outside them, however open admission is.
        Assert.Equal(30, authorization.Symbols.Count);

        foreach (var target in SecEdgarFilingDocumentPartition.TargetsFor(1))
        {
            Assert.Contains(target, authorization.Symbols);
        }

        // An expired authorisation refuses regardless of admission.
        Assert.False(authorization.IsValidAt(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)).Allowed);
    }

    /// <summary>Every batch is still unapproved, which is the gate admission does not touch.</summary>
    [Fact]
    public void Opening_admission_would_approve_no_batch()
    {
        Assert.All(SecEdgarFilingDocumentPartition.Batches, b => Assert.False(b.Authorised));
        Assert.Equal(6, SecEdgarFilingDocumentPartition.Batches.Length);
        Assert.Equal(30, SecEdgarFilingDocumentPartition.Planned);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>
    /// Options with no credential. The definition describes what a source supplies, not how it is
    /// reached, so an empty key changes nothing about the categories under test.
    /// </summary>
    private static IOptions<EodhdOptions> EodhdOptions() =>
        Microsoft.Extensions.Options.Options.Create(new EodhdOptions());

    /// <summary>The union exactly as <c>ReconcileSourceCoverageHandler</c> computes it.</summary>
    private static List<DataCategory> Union(
        IEnumerable<DataCategory> current,
        IEnumerable<DataCategory> shipped) =>
        current.Union(shipped).OrderBy(category => (int)category).ToList();

    private static DataSource Active(DataSource source)
    {
        source.Activate(Now);

        return source;
    }

    private static DataSource SourceWith(IEnumerable<DataCategory> categories) =>
        DataSource.Register(
            SecEdgarProvider.Id,
            "U.S. Securities and Exchange Commission - EDGAR",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            categories,
            UpdateCadence.EventDriven,
            LicensingTerms.Create(
                storageAllowed: true,
                redistributionAllowed: true,
                automatedProcessingAllowed: true,
                attributionRequired: true,
                notes: SecEdgarSource.LicensingNotes,
                retention: RetentionLimit.Unlimited),
            VerificationPolicy.Authoritative,
            Now,
            "Built in memory to model a registry state. It admits nothing anywhere.");
}
