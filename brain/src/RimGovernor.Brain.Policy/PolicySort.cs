namespace RimGovernor.Brain.Policy;

/// <summary>List-style helpers over <see cref="EquatableArray{T}"/> for the ported Go slice idioms.</summary>
internal static class EquatableArrayExtensions
{
    /// <summary>Go's <c>slices.IndexFunc</c>.</summary>
    public static int FindIndex<T>(this EquatableArray<T> array, Predicate<T> match)
    {
        for (var i = 0; i < array.Count; i++)
        {
            if (match(array[i]))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Stable sorting by a Go-style <c>less</c> function (Go's <c>sort.Slice</c> ties are arbitrary; stable is a refinement).</summary>
public static class PolicySort
{
    public static List<T> ByLess<T>(IEnumerable<T> items, Func<T, T, bool> less)
    {
        var comparer = Comparer<T>.Create((a, b) => less(a, b) ? -1 : less(b, a) ? 1 : 0);
        return [.. items.OrderBy(x => x, comparer)];
    }
}
