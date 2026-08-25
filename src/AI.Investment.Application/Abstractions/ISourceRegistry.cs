using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Abstractions;

/// <summary>Reads and writes registered data sources.</summary>
/// <remarks>
/// <para>
/// The read side is what every other part of the data plane depends on: a claim's provenance
/// names a <see cref="SourceId"/>, and answering "how much does this count?" means resolving that
/// identifier to the registered source behind it.
/// </para>
/// <para>
/// <see cref="Add"/> stages a registration; nothing is persisted until
/// <see cref="IUnitOfWork.SaveChangesAsync"/>, which itself requires an authorised execution to
/// be in progress. Registering a source is a side effect like any other and goes through the
/// Action/Policy seam under <see cref="Domain.Enums.Capability.ReferenceDataManagement"/>.
/// </para>
/// </remarks>
public interface ISourceRegistry
{
    Task<DataSource?> GetByIdAsync(SourceId id, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(SourceId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every registered source that declares <paramref name="category"/> and covers
    /// <paramref name="region"/>, whether or not it is currently admissible.
    /// </summary>
    /// <remarks>
    /// Returns inactive and unlicensed sources too, deliberately. Filtering belongs to
    /// <see cref="SourceAdmission"/>, which is pure and testable; a repository that silently
    /// dropped rows would make the reason for an empty result invisible and put a licensing rule
    /// inside a SQL query.
    /// </remarks>
    Task<IReadOnlyList<DataSource>> FindSuppliersAsync(
        DataCategory category,
        Region region,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DataSource>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Stages a new registration.</summary>
    void Add(DataSource source);
}
