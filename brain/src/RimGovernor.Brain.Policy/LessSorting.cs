namespace RimGovernor.Brain.Policy;

/// <summary>
/// A <c>less</c> function applied as a stable sort. Every call site orders
/// by a total key, so a stable sort is deterministic.
/// </summary>
internal static class LessSorting
{
    public static List<T> SortedBy<T>(this IEnumerable<T> items, Func<T, T, bool> less) =>
        [.. items.Order(Comparer<T>.Create((a, b) => less(a, b) ? -1 : less(b, a) ? 1 : 0))];
}
