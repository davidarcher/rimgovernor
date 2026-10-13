namespace RimGovernor.Brain.Policy;

/// <summary>
/// Go's <c>sort.Slice(s, less)</c> as a stable sort. The Go call is unstable; every ported call site orders
/// by a total key, so a stable sort is at least as deterministic.
/// </summary>
internal static class LessSorting
{
    public static List<T> SortedBy<T>(this IEnumerable<T> items, Func<T, T, bool> less) =>
        [.. items.Order(Comparer<T>.Create((a, b) => less(a, b) ? -1 : less(b, a) ? 1 : 0))];
}
