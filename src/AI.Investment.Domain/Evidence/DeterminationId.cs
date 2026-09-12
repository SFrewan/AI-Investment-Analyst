namespace AI.Investment.Domain.Evidence;

/// <summary>The identity of one recorded determination.</summary>
/// <remarks>
/// <para>
/// A surrogate, shaped after <c>CompanyId</c>, <c>ObservationId</c> and <c>SecurityId</c> so the
/// family reads alike and none can be passed where another is expected.
/// </para>
/// <para>
/// <strong>This is not what makes two determinations the same.</strong> Identity of <em>record</em>
/// is this id; identity of <em>result</em> is the reproducibility key - the rule, its version, the
/// as-of instant and the ordered input hashes. Two rows with different ids and the same key are the
/// same determination computed twice, which the unique index refuses.
/// </para>
/// </remarks>
public readonly record struct DeterminationId(Guid Value)
{
    public static DeterminationId New() => new(Guid.NewGuid());

    public static DeterminationId Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "A determination id may not be the empty GUID.",
                nameof(value));
        }

        return new DeterminationId(value);
    }

    public override string ToString() =>
        Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
