using System.Numerics;
using System.Text;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>Table[TValue]</c>: a persistent map from row id to row, a hash-array mapped trie with structural sharing.
/// <see cref="Set"/> and <see cref="Delete"/> return a new version in O(log n) that shares every untouched node with
/// the old one, and a version is never modified, so a consumer may keep one across frames and read it from any
/// thread. The default value is an empty table. Ids compare ordinally.
/// </summary>
public readonly struct Table<TValue>
{
    private const int Bits = 5;
    private const uint Mask = (1 << Bits) - 1;

    private readonly Node? _root;

    private Table(Node? root, int count)
    {
        _root = root;
        Count = count;
    }

    /// <summary>The number of rows (Go's <c>Len</c>).</summary>
    public int Count { get; }

    /// <summary>The row of <paramref name="id"/> (Go's <c>Get</c>).</summary>
    public bool TryGetValue(string id, out TValue value)
    {
        var node = _root;
        if (node is null)
        {
            value = default!;
            return false;
        }
        var h = HashKey(id);
        for (var shift = 0; ; shift += Bits)
        {
            if (shift >= 64)
            {
                foreach (var s in node.Slots)
                {
                    if (string.Equals(s.Key, id, StringComparison.Ordinal))
                    {
                        value = s.Val;
                        return true;
                    }
                }
                value = default!;
                return false;
            }
            var bit = 1u << (int)((h >> shift) & Mask);
            if ((node.Bitmap & bit) == 0)
            {
                value = default!;
                return false;
            }
            var slot = node.Slots[BitOperations.PopCount(node.Bitmap & (bit - 1))];
            if (slot.Child is null)
            {
                if (string.Equals(slot.Key, id, StringComparison.Ordinal))
                {
                    value = slot.Val;
                    return true;
                }
                value = default!;
                return false;
            }
            node = slot.Child;
        }
    }

    /// <summary>The row of <paramref name="id"/>, the type's default (Go's zero value) when absent (Go's <c>At</c>).</summary>
    public TValue? At(string id) => TryGetValue(id, out var v) ? v : default;

    /// <summary>Whether the table holds <paramref name="id"/>.</summary>
    public bool Has(string id) => TryGetValue(id, out _);

    /// <summary>The table with <paramref name="id"/>'s row replaced or added.</summary>
    public Table<TValue> Set(string id, TValue v)
    {
        var (root, added) = SetNode(_root, 0, HashKey(id), id, v);
        return new Table<TValue>(root, added ? Count + 1 : Count);
    }

    /// <summary>The table without <paramref name="id"/>.</summary>
    public Table<TValue> Delete(string id)
    {
        if (_root is null)
        {
            return this;
        }
        var (root, removed) = DeleteNode(_root, 0, HashKey(id), id);
        if (!removed)
        {
            return this;
        }
        return Count - 1 == 0 ? default : new Table<TValue>(root, Count - 1);
    }

    /// <summary>The rows in a fixed order for a given content; callers that need id order use <see cref="Sorted"/>.</summary>
    public IEnumerable<KeyValuePair<string, TValue>> All()
    {
        if (_root is null)
        {
            yield break;
        }
        foreach (var pair in Each(_root))
        {
            yield return pair;
        }
    }

    /// <summary>The rows in <see cref="All"/>'s order.</summary>
    public IEnumerable<TValue> Values() => All().Select(pair => pair.Value);

    /// <summary>The ids in sorted (ordinal) order.</summary>
    public IReadOnlyList<string> Keys() => [.. All().Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    /// <summary>The rows in id order.</summary>
    public IReadOnlyList<TValue> Sorted() => [.. All().OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value)];

    /// <summary>The rows as a plain dictionary: a copy, for the readers that publish a whole table.</summary>
    public Dictionary<string, TValue> ToDictionary()
    {
        var map = new Dictionary<string, TValue>(Count, StringComparer.Ordinal);
        foreach (var (key, value) in All())
        {
            map[key] = value;
        }
        return map;
    }

    /// <summary>
    /// Reports how this table differs from <paramref name="prev"/>: each row that is new or holds a different value
    /// (reference rows compare by identity) to <paramref name="changed"/>, each id <paramref name="prev"/> held that
    /// this table lacks to <paramref name="removed"/>. Subtrees the two versions share are skipped, so the cost
    /// follows the rows that changed between them, not the table's size.
    /// </summary>
    public void Changed(Table<TValue> prev, Action<string, TValue> changed, Action<string> removed) =>
        DiffNodes(_root, prev._root, 0, changed, removed);

    private static ulong HashKey(string key)
    {
        var h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        // Spread the low bits FNV leaves weak.
        h ^= h >> 32;
        h *= 0x9e3779b97f4a7c15UL;
        h ^= h >> 29;
        return h;
    }

    private static IEnumerable<KeyValuePair<string, TValue>> Each(Node node)
    {
        foreach (var s in node.Slots)
        {
            if (s.Child is not null)
            {
                foreach (var pair in Each(s.Child))
                {
                    yield return pair;
                }
            }
            else
            {
                yield return new KeyValuePair<string, TValue>(s.Key!, s.Val);
            }
        }
    }

    private static (Node, bool) SetNode(Node? n, int shift, ulong h, string key, TValue v)
    {
        var leaf = new Slot(null, key, h, v);
        if (n is null)
        {
            return (new Node(1u << (int)((h >> shift) & Mask), [leaf]), true);
        }
        if (shift >= 64)
        {
            for (var i = 0; i < n.Slots.Length; i++)
            {
                if (string.Equals(n.Slots[i].Key, key, StringComparison.Ordinal))
                {
                    return (new Node(0, Replace(n.Slots, i, leaf)), false);
                }
            }
            return (new Node(0, [.. n.Slots, leaf]), true);
        }
        var bit = 1u << (int)((h >> shift) & Mask);
        var pos = BitOperations.PopCount(n.Bitmap & (bit - 1));
        if ((n.Bitmap & bit) == 0)
        {
            var slots = new Slot[n.Slots.Length + 1];
            Array.Copy(n.Slots, 0, slots, 0, pos);
            slots[pos] = leaf;
            Array.Copy(n.Slots, pos, slots, pos + 1, n.Slots.Length - pos);
            return (new Node(n.Bitmap | bit, slots), true);
        }
        var s = n.Slots[pos];
        if (s.Child is not null)
        {
            var (child, added) = SetNode(s.Child, shift + Bits, h, key, v);
            return (new Node(n.Bitmap, Replace(n.Slots, pos, new Slot(child, null, 0, default!))), added);
        }
        if (string.Equals(s.Key, key, StringComparison.Ordinal))
        {
            return (new Node(n.Bitmap, Replace(n.Slots, pos, leaf)), false);
        }
        var merged = Merge(shift + Bits, s, leaf);
        return (new Node(n.Bitmap, Replace(n.Slots, pos, new Slot(merged, null, 0, default!))), true);
    }

    /// <summary>The node holding two leaves with different keys.</summary>
    private static Node Merge(int shift, Slot a, Slot b)
    {
        if (shift >= 64)
        {
            return new Node(0, [a, b]);
        }
        var ia = (a.Hash >> shift) & Mask;
        var ib = (b.Hash >> shift) & Mask;
        if (ia == ib)
        {
            return new Node(1u << (int)ia, [new Slot(Merge(shift + Bits, a, b), null, 0, default!)]);
        }
        Slot[] slots = ia > ib ? [b, a] : [a, b];
        return new Node((1u << (int)ia) | (1u << (int)ib), slots);
    }

    private static (Node?, bool) DeleteNode(Node n, int shift, ulong h, string key)
    {
        if (shift >= 64)
        {
            for (var i = 0; i < n.Slots.Length; i++)
            {
                if (string.Equals(n.Slots[i].Key, key, StringComparison.Ordinal))
                {
                    return n.Slots.Length == 1 ? (null, true) : (new Node(0, Without(n.Slots, i)), true);
                }
            }
            return (n, false);
        }
        var bit = 1u << (int)((h >> shift) & Mask);
        if ((n.Bitmap & bit) == 0)
        {
            return (n, false);
        }
        var pos = BitOperations.PopCount(n.Bitmap & (bit - 1));
        var s = n.Slots[pos];
        Slot repl;
        if (s.Child is not null)
        {
            var (child, removed) = DeleteNode(s.Child, shift + Bits, h, key);
            if (!removed)
            {
                return (n, false);
            }
            if (child is null)
            {
                return (Remove(n, pos, bit), true);
            }
            // A branch left with one leaf collapses into it.
            repl = child.Slots.Length == 1 && child.Slots[0].Child is null ? child.Slots[0] : new Slot(child, null, 0, default!);
        }
        else
        {
            if (!string.Equals(s.Key, key, StringComparison.Ordinal))
            {
                return (n, false);
            }
            return (Remove(n, pos, bit), true);
        }
        return (new Node(n.Bitmap, Replace(n.Slots, pos, repl)), true);
    }

    private static Node? Remove(Node n, int pos, uint bit) =>
        n.Slots.Length == 1 ? null : new Node(n.Bitmap & ~bit, Without(n.Slots, pos));

    private static Slot[] Replace(Slot[] slots, int i, Slot s)
    {
        var copy = (Slot[])slots.Clone();
        copy[i] = s;
        return copy;
    }

    private static Slot[] Without(Slot[] slots, int i)
    {
        var copy = new Slot[slots.Length - 1];
        Array.Copy(slots, 0, copy, 0, i);
        Array.Copy(slots, i + 1, copy, i, slots.Length - i - 1);
        return copy;
    }

    private static void DiffNodes(Node? a, Node? b, int shift, Action<string, TValue> changed, Action<string> removed)
    {
        if (ReferenceEquals(a, b))
        {
            return;
        }
        if (a is null || b is null || shift >= 64)
        {
            DiffLeaves(LeavesOf(a), LeavesOf(b), changed, removed);
            return;
        }
        for (var bit = 1u; ; bit <<= 1)
        {
            var inA = (a.Bitmap & bit) != 0;
            var inB = (b.Bitmap & bit) != 0;
            Slot sa = default;
            Slot sb = default;
            if (inA)
            {
                sa = a.Slots[BitOperations.PopCount(a.Bitmap & (bit - 1))];
            }
            if (inB)
            {
                sb = b.Slots[BitOperations.PopCount(b.Bitmap & (bit - 1))];
            }
            if (inA && inB && sa.Child is not null && sb.Child is not null)
            {
                DiffNodes(sa.Child, sb.Child, shift + Bits, changed, removed);
            }
            else if (inA || inB)
            {
                DiffLeaves(SlotLeaves(sa, inA), SlotLeaves(sb, inB), changed, removed);
            }
            if (bit == 1u << 31)
            {
                return;
            }
        }
    }

    private static List<KeyValuePair<string, TValue>> LeavesOf(Node? n) => n is null ? [] : [.. Each(n)];

    private static List<KeyValuePair<string, TValue>> SlotLeaves(Slot s, bool present)
    {
        if (!present)
        {
            return [];
        }
        return s.Child is not null ? LeavesOf(s.Child) : [new KeyValuePair<string, TValue>(s.Key!, s.Val)];
    }

    private static void DiffLeaves(List<KeyValuePair<string, TValue>> a, List<KeyValuePair<string, TValue>> b, Action<string, TValue> changed, Action<string> removed)
    {
        var old = new Dictionary<string, TValue>(b.Count, StringComparer.Ordinal);
        foreach (var leaf in b)
        {
            old[leaf.Key] = leaf.Value;
        }
        foreach (var leaf in a)
        {
            if (!old.TryGetValue(leaf.Key, out var prev) || !SameRow(prev, leaf.Value))
            {
                changed(leaf.Key, leaf.Value);
            }
            old.Remove(leaf.Key);
        }
        foreach (var leaf in b)
        {
            if (old.ContainsKey(leaf.Key))
            {
                removed(leaf.Key);
            }
        }
    }

    // Go compares the rows as interface values: pointer rows by identity, value rows by value.
    private static bool SameRow(TValue a, TValue b) =>
        typeof(TValue).IsValueType ? EqualityComparer<TValue>.Default.Equals(a, b) : ReferenceEquals(a, b);

    private sealed class Node(uint bitmap, Slot[] slots)
    {
        // Marks the occupied branches; Slots holds them in branch order. Below the last hash bit a node is a collision
        // bucket: every slot a leaf, searched linearly.
        public uint Bitmap { get; } = bitmap;

        public Slot[] Slots { get; } = slots;
    }

    // A leaf (Child null) or a branch.
    private readonly record struct Slot(Node? Child, string? Key, ulong Hash, TValue Val);
}

/// <summary>Go's <c>TableOf</c>.</summary>
public static class TableExtensions
{
    /// <summary>The table of a map's rows.</summary>
    public static Table<TValue> ToTable<TValue>(this IEnumerable<KeyValuePair<string, TValue>> rows)
    {
        var table = default(Table<TValue>);
        foreach (var (id, row) in rows)
        {
            table = table.Set(id, row);
        }
        return table;
    }
}
