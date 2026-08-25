namespace AI.Investment.Application.Common;

/// <summary>A page of results together with the total available.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Skip, int Take);

/// <summary>
/// Factories for <see cref="PagedResult{T}"/>.
/// </summary>
/// <remarks>
/// A non-generic host rather than statics on the generic type itself (CA1000). Two reasons, and
/// the second is the one that matters day to day: a static on a generic type can only be reached
/// by naming the type argument explicitly, whereas a generic method infers it - so this reads
/// <c>PagedResult.Empty&lt;CompanyDto&gt;(skip, take)</c> at worst and infers at best. The same
/// pattern is used for <c>Claims</c> in the domain.
/// </remarks>
public static class PagedResult
{
    public static PagedResult<T> Empty<T>(int skip, int take) => new([], 0, skip, take);

    public static PagedResult<T> From<T>(IReadOnlyList<T> items, int totalCount, int skip, int take) =>
        new(items, totalCount, skip, take);
}
