namespace AI.Investment.Application.Freshness;

/// <summary>One source's currency, as returned across the application boundary.</summary>
/// <remarks>
/// <para>
/// <see cref="RuleId"/> is on the wire deliberately. A report that says a source is not scheduled
/// without saying whether that is because it is switched off or because it publishes on events has
/// told an operator half of what they need, and the half it withheld is the actionable one.
/// </para>
/// <para>
/// <see cref="ElapsedSeconds"/> is null when the source has never been ingested. Null is not zero:
/// zero would mean "refreshed just now", which is the opposite of never having run.
/// </para>
/// </remarks>
public sealed record FreshnessDto(
    string SourceId,
    string Name,
    string Cadence,
    bool IsActive,
    string State,
    string RuleId,
    DateTime? LastRefreshedAtUtc,
    double? ElapsedSeconds,
    bool NeedsRefresh);

/// <summary>Maps freshness lines to their wire shape.</summary>
public static class FreshnessMapper
{
    public static FreshnessDto ToDto(SourceFreshness line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new FreshnessDto(
            line.SourceId.Value,
            line.Name,
            // The kind, not the value object's ToString() - same reason as SourceDto. Found by
            // sweeping after the SourceDto defect rather than by a failing test.
            line.Cadence.Kind.ToString(),
            line.IsActive,
            line.Assessment.State.ToString(),
            line.Assessment.RuleId,
            line.Assessment.LastRefreshedAtUtc,
            line.Assessment.Elapsed?.TotalSeconds,
            line.NeedsRefresh);
    }
}
