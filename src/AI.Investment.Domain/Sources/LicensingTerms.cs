using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Sources;

/// <summary>
/// What this system is permitted to DO with a source's data.
/// </summary>
/// <remarks>
/// <para>
/// A first-class field rather than a note in a wiki, because these are preconditions the platform
/// must be able to check in code. News and market-data licences vary enormously on the two
/// questions that matter most here: may the content be STORED, and may it be fed to a model.
/// A platform whose whole design is "keep the raw response forever and let agents read it" can
/// violate a licence by operating exactly as intended.
/// </para>
/// <para>
/// Every permission defaults to false. A source registered without stated terms is treated as
/// permitting nothing, which fails in the direction that is merely inconvenient rather than the
/// direction that is a breach. This is the same fail-closed principle the policy engine uses.
/// </para>
/// <para>
/// This models the terms; it does not interpret them. Deciding what a particular licence permits
/// is a question for a lawyer, and the registry is where that answer gets written down once
/// rather than re-litigated at each call site.
/// </para>
/// </remarks>
public sealed record LicensingTerms
{
    public const int MaxNotesLength = 1000;

    private LicensingTerms(
        bool storageAllowed,
        bool redistributionAllowed,
        bool automatedProcessingAllowed,
        bool attributionRequired,
        RetentionLimit retention,
        string? notes)
    {
        StorageAllowed = storageAllowed;
        RedistributionAllowed = redistributionAllowed;
        AutomatedProcessingAllowed = automatedProcessingAllowed;
        AttributionRequired = attributionRequired;
        Retention = retention;
        Notes = notes;
    }

    /// <summary>May the raw response be retained? If false, only derived values may be kept.</summary>
    public bool StorageAllowed { get; }

    /// <summary>May the content be shown to anyone outside this system?</summary>
    public bool RedistributionAllowed { get; }

    /// <summary>
    /// May the content be processed automatically, including by a language model? The permission
    /// most often withheld, and the one the analysis phases depend on.
    /// </summary>
    public bool AutomatedProcessingAllowed { get; }

    public bool AttributionRequired { get; }

    /// <summary>Free text: licence name, URL, or the specific clause that governs.</summary>
    /// <summary>
    /// How long the terms permit the data to be kept.
    /// </summary>
    /// <remarks>
    /// The authoritative statement of this source's retention obligation. Enforcement reads it
    /// from here rather than from configuration, so a rule and the terms it implements cannot
    /// drift apart - and adding a provider with a different obligation is a registration, not a
    /// change to the retention engine.
    /// </remarks>
    public RetentionLimit Retention { get; }

    public string? Notes { get; }

    /// <summary>
    /// Public-domain or open-licence terms - a government dataset, a regulatory filing.
    /// </summary>
    public static LicensingTerms OpenData(string? notes = null) =>
        Create(
            storageAllowed: true,
            redistributionAllowed: true,
            automatedProcessingAllowed: true,
            attributionRequired: false,
            // Open data carries no retention obligation. That is a fact about the licence, not a
            // default chosen for convenience.
            retention: RetentionLimit.Unlimited,
            notes: notes);

    /// <summary>
    /// Nothing is permitted. The default for a source whose terms have not been established.
    /// </summary>
    public static LicensingTerms Unknown { get; } =
        new(false, false, false, true, RetentionLimit.Unlimited,
            "Terms not established. Treated as permitting nothing until reviewed.");

    /// <summary>
    /// Records what a source's terms permit.
    /// </summary>
    /// <param name="retention">
    /// The retention obligation. Defaults to <see cref="RetentionLimit.Unlimited"/> - no
    /// <em>legal</em> compulsion to delete - which is safe because an unestablished licence is
    /// <see cref="Unknown"/> and permits no ingestion at all, so nothing is ever stored under
    /// terms nobody has read.
    /// </param>
    public static LicensingTerms Create(
        bool storageAllowed,
        bool redistributionAllowed,
        bool automatedProcessingAllowed,
        bool attributionRequired,
        string? notes = null,
        RetentionLimit? retention = null)
    {
        string? trimmed = null;

        if (!string.IsNullOrWhiteSpace(notes))
        {
            trimmed = notes.Trim();

            if (trimmed.Length > MaxNotesLength)
            {
                throw new DomainValidationException(
                    nameof(notes),
                    $"Licensing notes may not exceed {MaxNotesLength} characters.");
            }
        }

        return new LicensingTerms(
            storageAllowed,
            redistributionAllowed,
            automatedProcessingAllowed,
            attributionRequired,
            retention ?? RetentionLimit.Unlimited,
            trimmed);
    }

    public override string ToString() =>
        $"store={StorageAllowed}, redistribute={RedistributionAllowed}, " +
        $"automatedProcessing={AutomatedProcessingAllowed}";
}
