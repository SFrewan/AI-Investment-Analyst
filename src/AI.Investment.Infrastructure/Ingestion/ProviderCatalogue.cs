using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Infrastructure.Ingestion;

/// <summary>
/// The connectors this installation has registered.
/// </summary>
/// <remarks>
/// <para>
/// Built from whatever <see cref="IDataProvider"/> implementations the container holds, so adding
/// a provider is one registration and nothing else - no switch statement to extend, no factory to
/// edit. That is what keeps the platform's reach open-ended: market data, fundamentals, news,
/// macroeconomic series, regulatory records, product catalogues and whatever a later opportunity
/// domain needs are all the same shape to everything above this line.
/// </para>
/// <para>
/// A connector that is configured out - EDGAR without a contact address, a paid feed without a
/// key - is simply absent here, and the gateway refuses runs for its source with a named rule
/// that lands in the ledger. Absent and explained beats present and broken.
/// </para>
/// <para>
/// Two connectors claiming the same source is a configuration error rather than a race to be
/// resolved silently, so it is refused at construction: the alternative is a deployment where
/// which one answers depends on registration order.
/// </para>
/// </remarks>
public sealed class ProviderCatalogue : IProviderCatalogue
{
    private readonly Dictionary<string, IDataProvider> _bySource;
    private readonly List<IDataProvider> _all;

    public ProviderCatalogue(IEnumerable<IDataProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _all = providers.ToList();
        _bySource = new Dictionary<string, IDataProvider>(StringComparer.Ordinal);

        foreach (var provider in _all)
        {
            if (!_bySource.TryAdd(provider.SourceId.Value, provider))
            {
                throw new InvalidOperationException(
                    $"Two connectors are registered for source '{provider.SourceId}'. A source has " +
                    "exactly one transport; which of them answered would otherwise depend on " +
                    "registration order.");
            }
        }
    }

    public IDataProvider? Find(SourceId sourceId)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        return _bySource.TryGetValue(sourceId.Value, out var provider) ? provider : null;
    }

    public IReadOnlyList<IDataProvider> All() => _all;
}
