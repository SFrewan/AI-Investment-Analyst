using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Ingestion;

/// <summary>Identity of a single ingestion run.</summary>
/// <remarks>
/// A distinct type rather than a bare <see cref="Guid"/>, for the same reason every other
/// identity in this domain is: a run identity passed where a claim or proposal identity is
/// expected should be a compiler error rather than a support ticket.
/// </remarks>
public readonly record struct IngestionRunId(Guid Value)
{
    public static IngestionRunId New() => new(Guid.NewGuid());

    public static IngestionRunId Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException(nameof(value), "An ingestion run identifier may not be empty.");
        }

        return new IngestionRunId(value);
    }

    public override string ToString() => Value.ToString("d", CultureInfo.InvariantCulture);
}
