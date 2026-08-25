using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace AI.Investment.Infrastructure.Persistence.Repositories;

/// <summary>
/// Answers whether stored evidence still points at an archived payload.
/// </summary>
/// <remarks>
/// <para>
/// Uses PostgreSQL's <c>jsonb</c> containment operator against
/// <c>ingestion_runs.artifacts</c>. Raw SQL rather than LINQ, deliberately: the column is mapped
/// through a value converter, so EF sees a <c>List&lt;ContentHash&gt;</c> it cannot translate, and
/// the alternative to one containment query is loading every run and scanning in memory. That
/// would turn a retention sweep into a full table read per payload.
/// </para>
/// <para>
/// <strong>Errs toward "referenced".</strong> The retention floor depends on this answer, and the
/// two mistakes are not equal: a false positive keeps a payload that could have gone, costing
/// disk; a false negative deletes evidence something relied on, which cannot be undone.
/// </para>
/// <para>
/// Ingestion runs are the only reference source today because they are the only persisted thing
/// that names a content hash. When claims are persisted this becomes a union of two queries, and
/// the interface does not change - which is why the caller asks a question rather than running a
/// query.
/// </para>
/// </remarks>
public sealed class EfPayloadReferenceIndex : IPayloadReferenceIndex
{
    private readonly AppDbContext _dbContext;

    public EfPayloadReferenceIndex(AppDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<bool> IsReferencedAsync(
        ContentHash hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        // A one-element JSON array is the containment probe: artifacts @> '["<hash>"]'.
        // Interpolated into a FormattableString, so EF parameterises it rather than concatenating
        // it - the hash is already constrained to 64 hex characters, but a query built by string
        // concatenation is a habit worth not having.
        var probe = $"[\"{hash.Value}\"]";

        var results = await _dbContext.Database
            .SqlQuery<bool>(
                $"""
                 SELECT EXISTS (
                     SELECT 1 FROM ingestion_runs WHERE artifacts @> {probe}::jsonb
                 ) AS "Value"
                 """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return results.Count == 0 || results[0];
    }
}
