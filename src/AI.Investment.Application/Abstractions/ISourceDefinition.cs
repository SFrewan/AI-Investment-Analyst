using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Abstractions;

/// <summary>
/// A source this installation ships a definition for.
/// </summary>
/// <remarks>
/// <para>
/// The registry is normally filled by an operator, but a connector that ships in the box knows its
/// own source's authority, licensing, coverage and cadence better than anyone re-typing them
/// would. Exposing that as a definition lets the platform seed itself accurately without anyone
/// hard-coding a provider anywhere above Infrastructure.
/// </para>
/// <para>
/// <strong>A definition is not a registration.</strong> Implementations return an inactive source,
/// registration goes through the Action/Policy seam like any other side effect, and activation is
/// a separate deliberate act. Shipping a connector does not switch it on.
/// </para>
/// </remarks>
public interface ISourceDefinition
{
    SourceId SourceId { get; }

    /// <summary>Builds the registry entry, inactive.</summary>
    DataSource Definition(DateTime nowUtc);
}
