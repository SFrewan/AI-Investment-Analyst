namespace AI.Investment.Domain.Sources;

/// <summary>
/// Decides whether a registered source may be drawn from for a given category and region.
/// </summary>
/// <remarks>
/// <para>
/// The data plane's equivalent of the policy engine, and built to the same specification: pure,
/// total, deterministic and fail-closed. The same source, category and region always produce the
/// same answer, and every refusal names the rule that produced it.
/// </para>
/// <para>
/// It exists so that the question "may we ingest this?" is answered in one place rather than by
/// each connector's own scattering of if-statements. The licensing rules in particular must not
/// live in connector code: a connector is written to talk to an API, and whether the platform is
/// permitted to store what comes back is not a transport concern.
/// </para>
/// <para>
/// This checks the <em>source's</em> standing only. It says nothing about whether the resulting
/// data is any good - that is validation, and it happens after retrieval.
/// </para>
/// </remarks>
public static class SourceAdmission
{
    public const string SourceActiveRule = "source.active@1";
    public const string CategoryRecognisedRule = "source.category-recognised@1";
    public const string SuppliesCategoryRule = "source.supplies-category@1";
    public const string StoragePermittedRule = "source.storage-permitted@1";
    public const string ProcessingPermittedRule = "source.processing-permitted@1";

    /// <summary>
    /// Evaluates the rules in order and returns the first refusal, or
    /// <see cref="SourceAdmissionResult.Admitted"/>.
    /// </summary>
    public static SourceAdmissionResult Evaluate(DataSource source, DataCategory category, Region region)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(region);

        // 1. Registration is not permission. A source is registered inactive and stays that way
        //    until someone deliberately switches it on.
        if (!source.IsActive)
        {
            return SourceAdmissionResult.Refused(
                SourceActiveRule,
                $"Source '{source.Id}' is registered but not active. Registration records that a " +
                "source has been assessed, not that it may be used.");
        }

        // 2. Fail closed on an enum value this build does not recognise, exactly as the policy
        //    engine does. An unrecognised category is not a category to be generous about.
        if (!Enum.IsDefined(category) || category == DataCategory.Unknown)
        {
            return SourceAdmissionResult.Refused(
                CategoryRecognisedRule,
                $"'{category}' is not a data category this build recognises, so no source can be " +
                "said to supply it.");
        }

        // 3. Coverage. Declared categories AND regional reach - a source scoped to one market
        //    cannot answer for another, however authoritative it is at home.
        if (!source.Supplies(category, region))
        {
            return SourceAdmissionResult.Refused(
                SuppliesCategoryRule,
                $"Source '{source.Id}' does not supply {category} for {region}. Its declared " +
                $"region is {source.Region}, and its declared category count is " +
                $"{source.Categories.Count}.");
        }

        // 4 and 5. Licensing. Checked here rather than at retrieval because by the time a
        //    response has been fetched and written down, an impermissible ingestion has already
        //    happened. The terms default to permitting nothing, so an unassessed source fails
        //    these rules rather than slipping through them.
        if (!source.Licensing.StorageAllowed)
        {
            return SourceAdmissionResult.Refused(
                StoragePermittedRule,
                $"The licensing terms recorded for source '{source.Id}' do not permit storage, and " +
                "ingestion stores what it retrieves.");
        }

        if (!source.Licensing.AutomatedProcessingAllowed)
        {
            return SourceAdmissionResult.Refused(
                ProcessingPermittedRule,
                $"The licensing terms recorded for source '{source.Id}' do not permit automated " +
                "processing, which is the only kind this platform performs.");
        }

        return SourceAdmissionResult.Admitted;
    }

    /// <summary>
    /// Returns the admitted sources, most authoritative first.
    /// </summary>
    /// <remarks>
    /// The composition ingestion actually wants: of everything registered, which sources may be
    /// used for this purpose, and in what order should they be believed.
    /// </remarks>
    public static IReadOnlyList<DataSource> Admissible(
        IEnumerable<DataSource> sources,
        DataCategory category,
        Region region)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(region);

        var admitted = new List<DataSource>();

        foreach (var source in sources)
        {
            if (source is not null && Evaluate(source, category, region).IsAdmitted)
            {
                admitted.Add(source);
            }
        }

        return SourceRanking.MostAuthoritativeFirst(admitted);
    }
}
