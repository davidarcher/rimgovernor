using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace RimGovernor.Brain.Domain;

/// <summary>
/// An immutable sequence with value (element-wise) equality, for slice fields of records that compare
/// with <c>==</c>. Elements compare with <see cref="EqualityComparer{T}.Default"/>; the default value is empty.
/// </summary>
[CollectionBuilder(typeof(EquatableArray), nameof(EquatableArray.Create))]
public readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
{
    private readonly ImmutableArray<T> _items = items;

    public ImmutableArray<T> Items => _items.IsDefault ? [] : _items;

    public int Count => Items.Length;

    public T this[int index] => Items[index];

    public bool Equals(EquatableArray<T> other) => Items.AsSpan().SequenceEqual(other.Items.AsSpan());

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items)
        {
            hash.Add(item);
        }
        return hash.ToHashCode();
    }

    /// <summary>A struct enumerator, so foreach over an EquatableArray allocates nothing (#3468).</summary>
    public ImmutableArray<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    public static implicit operator EquatableArray<T>(ImmutableArray<T> items) => new(items);
}

public static class EquatableArray
{
    /// <summary>Copies <paramref name="items"/>.</summary>
    public static EquatableArray<T> From<T>(IEnumerable<T> items) => new([.. items]);

    /// <summary>The collection-expression builder: <c>EquatableArray&lt;T&gt; xs = [a, b];</c>.</summary>
    public static EquatableArray<T> Create<T>(ReadOnlySpan<T> items) => new([.. items]);

    /// <summary>Wraps <paramref name="owned"/> without copying (#3468): the caller gives up the array and never writes it again.</summary>
    public static EquatableArray<T> Wrap<T>(T[] owned) => new(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(owned));
}
