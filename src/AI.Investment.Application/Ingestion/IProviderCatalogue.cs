using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Ingestion;

/// <summary>Resolves a registered source to the connector that serves it.</summary>
/// <remarks>
/// The join between the trust model and the transport layer. A source with no connector is a
/// perfectly valid registry entry - it has been assessed but nothing can fetch from it yet - so
/// this returns null rather than throwing, and the caller records that as a refusal with a reason
/// instead of an exception nobody can explain later.
/// </remarks>
public interface IProviderCatalogue
{
    IDataProvider? Find(SourceId sourceId);

    IReadOnlyList<IDataProvider> All();
}
