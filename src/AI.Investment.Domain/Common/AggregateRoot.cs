namespace AI.Investment.Domain.Common;

/// <summary>
/// Base class for the consistency boundaries of the domain.
/// </summary>
/// <typeparam name="TId">The aggregate's strongly-typed identifier.</typeparam>
/// <remarks>
/// <para>
/// Aggregates are compared by identity, not by their contents: two loads of the same company
/// are the same company even if one has since been renamed. Value objects are the opposite, and
/// are records for exactly that reason.
/// </para>
/// <para>
/// Deliberately minimal. There is no domain-event dispatching here yet - nothing in Phase 1
/// raises a domain event, and an unused event pipeline is a speculative abstraction. It is
/// added when the first real subscriber exists.
/// </para>
/// </remarks>
public abstract class AggregateRoot<TId> : IEquatable<AggregateRoot<TId>>
    where TId : notnull
{
    protected AggregateRoot(TId id) => Id = id;

    /// <summary>Parameterless constructor for the persistence provider's materialisation only.</summary>
    protected AggregateRoot() => Id = default!;

    public TId Id { get; protected set; }

    public bool Equals(AggregateRoot<TId>? other) =>
        other is not null && GetType() == other.GetType() && Id.Equals(other.Id);

    public override bool Equals(object? obj) => Equals(obj as AggregateRoot<TId>);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
