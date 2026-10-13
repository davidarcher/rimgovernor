using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>ColonyRow</c>: one colony facts row, a message part or a keyed list's element keys in native order.
/// </summary>
public sealed record ColonyRow(object? Part, IReadOnlyList<string> Order) : IEquatable<ColonyRow>
{
    /// <summary>Go's <c>Equal</c>: compares two rows' facts.</summary>
    public bool Equals(ColonyRow? other)
    {
        if (other is null || (Part is null) != (other.Part is null))
        {
            return false;
        }
        return Part is not null ? Part.Equals(other.Part) : Order.SequenceEqual(other.Order, StringComparer.Ordinal);
    }

    public override int GetHashCode() => Part?.GetHashCode() ?? Order.Count;
}

// Go colony_rows.go: a ColonyFactsSnapshot splits into sections of native rows, one section per sub-section that
// holds keyed rows, and "colony" for the rest. SplitColonyFacts and JoinColonyFacts are ported; ParseColonyRow and
// ColonyRow.MarshalJSON are the mirror's JSON (de)serialisation and are not ported. The walk is by wire field name
// (snake_case) through WireMessage, so it reads like the descriptor walk it replaced.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>ColonySection</c>: the root colony facts section.</summary>
    public const string ColonySection = "colony";

    private static readonly string[] ColonyFallbackKeys = ["def_name"];

    private sealed record ColonyLayoutValue(
        string[] Sections,
        IReadOnlyDictionary<string, WireField> Field,
        IReadOnlyDictionary<string, WireField> Observed);

    private static readonly Lazy<ColonyLayoutValue> ColonyLayout = new(() =>
    {
        var names = new List<string>();
        var fields = new Dictionary<string, WireField>(StringComparer.Ordinal);
        var observed = new Dictionary<string, WireField>(StringComparer.Ordinal);
        foreach (var field in WireMessage.Of(typeof(ColonyFactsSnapshot)).Fields)
        {
            if (!OwnSection(field))
            {
                continue;
            }
            var name = ColonySection + "." + field.Name;
            names.Add(name);
            fields[name] = field;
            if (ObservedOutcome(field) is { } outcome)
            {
                observed[name] = outcome;
            }
        }
        names.Sort(StringComparer.Ordinal);
        return new ColonyLayoutValue([ColonySection, .. names], fields, observed);
    });

    /// <summary>Whether the field holds a message (or a list of them), not a scalar, enum or map.</summary>
    private static bool HoldsMessage(WireField field) =>
        field.Kind == WireKind.Message || (field.Kind == WireKind.Repeated && WireMessage.IsRecord(field.ValueType));

    /// <summary>
    /// Go's <c>observedOutcome</c>: a sub-section's oneof "observed" field, when no field of the observed message
    /// shares a name with the sub-section's own.
    /// </summary>
    private static WireField? ObservedOutcome(WireField field)
    {
        if (field.IsRepeated)
        {
            return null;
        }
        var section = WireMessage.Of(field.ValueType);
        var observed = section.Find("observed");
        if (observed is null || !observed.IsOneofCase || observed.Kind != WireKind.Message)
        {
            return null;
        }
        foreach (var inner in WireMessage.Of(observed.ValueType).Fields)
        {
            if (section.Find(inner.Name) is not null)
            {
                return null;
            }
        }
        return observed;
    }

    /// <summary>Go's <c>ColonySections</c>: names every colony facts section, the root first.</summary>
    public static IReadOnlyList<string> ColonySections() => [.. ColonyLayout.Value.Sections];

    private static bool OwnSection(WireField field)
    {
        if (!HoldsMessage(field) || field.Name == "delta")
        {
            return false;
        }
        return field.IsRepeated ? Keyable(field.ValueType) : HoldsKeyed(field.ValueType, 0);
    }

    /// <summary>Whether a list of <paramref name="message"/> can carry element keys.</summary>
    private static bool Keyable(Type message)
    {
        var schema = WireMessage.Of(message);
        if (IdField(message) is not null)
        {
            return true;
        }
        foreach (var field in schema.Fields)
        {
            if (field.Kind == WireKind.Message && IdField(field.ValueType) is not null)
            {
                return true;
            }
        }
        foreach (var name in ColonyFallbackKeys)
        {
            if (schema.Find(name) is { Kind: WireKind.Scalar } key && key.ValueType == typeof(string))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether <paramref name="message"/>, through singular message fields, holds a keyable list.</summary>
    private static bool HoldsKeyed(Type message, int depth)
    {
        if (depth > 8)
        {
            return false;
        }
        foreach (var field in WireMessage.Of(message).Fields)
        {
            if (!HoldsMessage(field))
            {
                continue;
            }
            if ((field.IsRepeated && Keyable(field.ValueType)) || (!field.IsRepeated && HoldsKeyed(field.ValueType, depth + 1)))
            {
                return true;
            }
        }
        return false;
    }

    private static WireField? IdField(Type message) =>
        WireMessage.Of(message).Find("id") is { Kind: WireKind.Scalar } field && field.ValueType == typeof(string) ? field : null;

    // ---- row keys ----------------------------------------------------------------------------------------------

    /// <summary>Go's <c>elementKey</c>: a repeated element's string id, else the id of its first singular message field whose type has one.</summary>
    private static string ElementKey(object element)
    {
        var type = element.GetType();
        if (IdField(type) is { } id)
        {
            return (string?)id.Get(element) ?? "";
        }
        foreach (var field in WireMessage.Of(type).Fields)
        {
            if (field.Kind != WireKind.Message)
            {
                continue;
            }
            if (IdField(field.ValueType) is not { } inner)
            {
                continue;
            }
            return field.Get(element) is { } child ? (string?)inner.Get(child) ?? "" : "";
        }
        return "";
    }

    /// <summary>Go's <c>rowKey</c>: an element's key, empty when it has none.</summary>
    private static string RowKey(object element)
    {
        var key = ElementKey(element);
        if (key.Length != 0)
        {
            return key;
        }
        foreach (var name in ColonyFallbackKeys)
        {
            if (WireMessage.Of(element).Find(name) is { Kind: WireKind.Scalar } field && field.ValueType == typeof(string))
            {
                return (string?)field.Get(element) ?? "";
            }
        }
        return "";
    }

    /// <summary>Go's <c>listKeys</c>: the list's element keys, null unless every element has a distinct one a key path can carry.</summary>
    private static List<string>? ListKeys(List<object?> list)
    {
        var keys = new List<string>(list.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list)
        {
            var key = RowKey(item!);
            if (key.Length == 0 || !seen.Add(key) || key.AsSpan().IndexOfAny('[', ']') >= 0)
            {
                return null;
            }
            keys.Add(key);
        }
        return keys;
    }

    // ---- split -------------------------------------------------------------------------------------------------

    /// <summary>Go's protoreflect <c>Range</c>: the populated fields of a message with their values, in declaration order.</summary>
    private static IEnumerable<(WireField Field, object Value)> PopulatedFields(object message) => WireMessage.PopulatedFields(message);

    /// <summary>Go's <c>partial</c>: <paramref name="parent"/> with only <paramref name="field"/> set. The records are immutable, so the value is shared.</summary>
    private static ColonyRow Partial(object parent, WireField field)
    {
        var result = WireMessage.Of(parent).New();
        field.Set(result, field.Get(parent));
        return new ColonyRow(result, []);
    }

    /// <summary>
    /// Go's <c>SplitColonyFacts</c>: <paramref name="v"/>'s rows by section; every section is present, empty when
    /// <paramref name="v"/> holds none of it. <paramref name="v"/> is not modified.
    /// </summary>
    public static Dictionary<string, Dictionary<string, ColonyRow>> SplitColonyFacts(ColonyFactsSnapshot v)
    {
        var layout = ColonyLayout.Value;
        var result = new Dictionary<string, Dictionary<string, ColonyRow>>(layout.Sections.Length, StringComparer.Ordinal);
        foreach (var name in layout.Sections)
        {
            result[name] = new Dictionary<string, ColonyRow>(StringComparer.Ordinal);
        }
        foreach (var (field, value) in PopulatedFields(v))
        {
            if (field.Name == "delta")
            {
                continue;
            }
            var name = ColonySection + "." + field.Name;
            if (!result.TryGetValue(name, out var rows))
            {
                result[ColonySection][field.Name] = Partial(v, field);
            }
            else if (field.IsRepeated)
            {
                SplitField(v, field, value, "", rows);
            }
            else
            {
                if (layout.Observed.TryGetValue(name, out var observed) && observed.Get(value) is { } inner)
                {
                    SplitMessage(inner, "", rows);
                    if (rows.Count > 0)
                    {
                        continue;
                    }
                }
                SplitMessage(value, "", rows);
                if (rows.Count == 0)
                {
                    // An empty sub-section is still present.
                    result[ColonySection][field.Name] = Partial(v, field);
                }
            }
        }
        return result;
    }

    private static void SplitMessage(object message, string prefix, Dictionary<string, ColonyRow> rows)
    {
        foreach (var (field, value) in PopulatedFields(message))
        {
            SplitField(message, field, value, prefix, rows);
        }
    }

    private static void SplitField(object parent, WireField field, object value, string prefix, Dictionary<string, ColonyRow> rows)
    {
        var key = prefix.Length == 0 ? field.Name : prefix + "." + field.Name;
        if (HoldsMessage(field))
        {
            if (field.IsRepeated)
            {
                var list = field.Elements(parent).ToList();
                if (ListKeys(list) is { } keys)
                {
                    for (var i = 0; i < keys.Count; i++)
                    {
                        rows[key + "[" + keys[i] + "]"] = new ColonyRow(list[i], []);
                    }
                    rows[key + "[]"] = new ColonyRow(null, keys);
                    return;
                }
            }
            else if (HoldsKeyed(field.ValueType, 0))
            {
                var before = rows.Count;
                SplitMessage(value, key, rows);
                if (rows.Count > before)
                {
                    return;
                }
            }
        }
        rows[key] = Partial(parent, field);
    }

    // ---- join --------------------------------------------------------------------------------------------------

    /// <summary>Go's <c>keySegment</c>: one step of a row key.</summary>
    private readonly record struct KeySegment(string Name, string Key, bool Keyed);

    /// <summary>Go's <c>parseRowKey</c>.</summary>
    private static List<KeySegment> ParseRowKey(string key)
    {
        var result = new List<KeySegment>();
        while (key.Length > 0)
        {
            var end = key.AsSpan().IndexOfAny('.', '[');
            string name;
            var segmentKey = "";
            var keyed = false;
            if (end < 0)
            {
                name = key;
                key = "";
            }
            else
            {
                name = key[..end];
                key = key[end..];
                if (key[0] == '[')
                {
                    var close = key.IndexOf(']', StringComparison.Ordinal);
                    if (close < 0)
                    {
                        throw new DomainException($"row key \"{key}\"");
                    }
                    segmentKey = key[1..close];
                    keyed = true;
                    key = key[(close + 1)..];
                }
                if (key.Length > 0)
                {
                    if (key[0] != '.')
                    {
                        throw new DomainException($"row key \"{key}\"");
                    }
                    key = key[1..];
                }
            }
            if (name.Length == 0)
            {
                throw new DomainException("row key segment");
            }
            result.Add(new KeySegment(name, segmentKey, keyed));
        }
        if (result.Count == 0)
        {
            throw new DomainException("empty row key");
        }
        for (var i = 0; i < result.Count - 1; i++)
        {
            if (result[i].Keyed)
            {
                throw new DomainException("row key descends into a list element");
            }
        }
        return result;
    }

    /// <summary>Go's <c>siteOf</c>: the steps of a row key and whether they sit within the sub-section's observed outcome.</summary>
    private static (List<KeySegment> Segments, bool Observed) SiteOf(string section, string key)
    {
        var layout = ColonyLayout.Value;
        var segments = ParseRowKey(key);
        if (section == ColonySection)
        {
            return (segments, false);
        }
        if (!layout.Field.TryGetValue(section, out var field))
        {
            throw new DomainException($"colony section \"{section}\"");
        }
        if (field.IsRepeated)
        {
            return (segments, false);
        }
        if (layout.Observed.ContainsKey(section) && WireMessage.Of(field.ValueType).Find(segments[0].Name) is null)
        {
            return (segments, true);
        }
        return (segments, false);
    }

    /// <summary>Go's <c>pendingList</c>.</summary>
    private sealed class PendingList(object parent, WireField field)
    {
        public object Parent { get; } = parent;

        public WireField Field { get; } = field;

        public IReadOnlyList<string> Order { get; set; } = [];

        public bool Ordered { get; set; }

        public Dictionary<string, object> Elements { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The singular message field of <paramref name="message"/>, created and attached when unset (protoreflect <c>Mutable</c>).</summary>
    private static object MutableMessage(object message, WireField field)
    {
        if (field.Get(message) is { } existing)
        {
            return existing;
        }
        var created = WireMessage.Of(field.ValueType).New();
        field.Set(message, created);
        return created;
    }

    /// <summary>
    /// Go's <c>JoinColonyFacts</c>: the snapshot the sections' rows describe (<see cref="SplitColonyFacts"/> reversed);
    /// a missing section contributes nothing.
    /// </summary>
    /// <exception cref="DomainException">The rows do not describe a snapshot.</exception>
    public static ColonyFactsSnapshot JoinColonyFacts(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ColonyRow>> sections)
    {
        var layout = ColonyLayout.Value;
        var result = new ColonyFactsSnapshot();
        foreach (var name in layout.Sections)
        {
            if (!sections.TryGetValue(name, out var rows) || rows.Count == 0)
            {
                continue;
            }
            object target = result;
            object? section = null;
            layout.Field.TryGetValue(name, out var field);
            layout.Observed.TryGetValue(name, out var observed);
            if (field is { IsRepeated: false })
            {
                section = WireMessage.Of(field.ValueType).New();
                target = section;
            }
            JoinRows(name, rows, target, section, observed);
            if (section is not null)
            {
                field!.Set(result, section);
            }
        }
        return result;
    }

    private static void JoinRows(string name, IReadOnlyDictionary<string, ColonyRow> rows, object target, object? section, WireField? observed)
    {
        var keys = new List<string>(rows.Keys);
        keys.Sort(StringComparer.Ordinal);
        var lists = new Dictionary<string, PendingList>(StringComparer.Ordinal);
        var listOrder = new List<string>();
        foreach (var key in keys)
        {
            var row = rows[key];
            var (segments, inObserved) = SiteOf(name, key);
            var message = target;
            if (inObserved)
            {
                message = MutableMessage(section!, observed!);
            }
            for (var i = 0; i < segments.Count - 1; i++)
            {
                var step = WireMessage.Of(message).Find(segments[i].Name);
                if (step is null || step.Kind != WireKind.Message)
                {
                    throw new DomainException($"row {name} {key}: field {segments[i].Name}");
                }
                message = MutableMessage(message, step);
            }
            var last = segments[^1];
            var field = WireMessage.Of(message).Find(last.Name) ?? throw new DomainException($"row {name} {key}: field {last.Name}");
            if (!last.Keyed)
            {
                if (row.Part is null || row.Part.GetType() != message.GetType())
                {
                    throw new DomainException($"row {name} {key}: part type");
                }
                var into = WireMessage.Of(message);
                foreach (var (partField, partValue) in PopulatedFields(row.Part))
                {
                    into.Find(partField.Name)!.Set(message, partValue);
                }
                continue;
            }
            var path = key[..key.LastIndexOf('[')];
            if (!lists.TryGetValue(path, out var list))
            {
                list = new PendingList(message, field);
                lists[path] = list;
                listOrder.Add(path);
            }
            if (last.Key.Length == 0)
            {
                list.Order = row.Order;
                list.Ordered = true;
                continue;
            }
            list.Elements[last.Key] = row.Part ?? throw new DomainException($"row {name} {key}: no element");
        }
        foreach (var path in listOrder)
        {
            var list = lists[path];
            if (!list.Ordered || list.Order.Count != list.Elements.Count)
            {
                throw new DomainException($"rows {name} {path}: order does not cover the elements");
            }
            var elements = new List<object?>(list.Order.Count);
            foreach (var key in list.Order)
            {
                if (!list.Elements.TryGetValue(key, out var element))
                {
                    throw new DomainException($"rows {name} {path}: no element {key}");
                }
                elements.Add(element);
            }
            list.Field.Set(list.Parent, list.Field.MakeArray(elements));
        }
    }
}
