namespace RimGovernor.Brain.Policy;

/// <summary>
/// Interns sequences to small ids (#3468): a search's evaluations repeat the same choice lists, occupancies and completed
/// sets thousands of times, so each distinct sequence is kept once and everything keyed on it is keyed on its id. Elements
/// compare by the given comparer (reference equality for the records a search already shares). The empty sequence is id 0.
/// </summary>
internal sealed class SequenceKeys<T>
{
    private readonly Dictionary<T[], int> ids;
    private readonly Dictionary<T[], int>.AlternateLookup<ReadOnlySpan<T>> spans;
    private readonly List<T[]> arrays = [];

    public SequenceKeys(IEqualityComparer<T>? elements = null)
    {
        ids = new(new Comparer(elements ?? EqualityComparer<T>.Default));
        spans = ids.GetAlternateLookup<ReadOnlySpan<T>>();
        Id(ReadOnlySpan<T>.Empty);
    }

    public int Id(ReadOnlySpan<T> items)
    {
        if (spans.TryGetValue(items, out var id)) return id;
        var array = items.ToArray();
        id = arrays.Count;
        arrays.Add(array);
        ids.Add(array, id);
        return id;
    }

    public int Id(List<T> items) => Id(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(items));

    /// <summary>The interned array itself: shared, never written.</summary>
    public T[] Array(int id) => arrays[id];

    /// <summary>The interned sequence as an EquatableArray over the shared array, nothing copied.</summary>
    public EquatableArray<T> Canonical(int id) => EquatableArray.Wrap(arrays[id]);

    private sealed class Comparer(IEqualityComparer<T> elements) : IEqualityComparer<T[]>, IAlternateEqualityComparer<ReadOnlySpan<T>, T[]>
    {
        public bool Equals(T[]? x, T[]? y) => Equals(new ReadOnlySpan<T>(x), y!);

        public int GetHashCode(T[] obj) => GetHashCode(new ReadOnlySpan<T>(obj));

        public bool Equals(ReadOnlySpan<T> alternate, T[] other)
        {
            if (alternate.Length != other.Length) return false;
            for (var i = 0; i < other.Length; i++)
                if (!elements.Equals(alternate[i], other[i])) return false;
            return true;
        }

        public int GetHashCode(ReadOnlySpan<T> alternate)
        {
            var hash = new HashCode();
            foreach (var item in alternate) hash.Add(item is null ? 0 : elements.GetHashCode(item));
            return hash.ToHashCode();
        }

        public T[] Create(ReadOnlySpan<T> alternate) => alternate.ToArray();
    }
}
