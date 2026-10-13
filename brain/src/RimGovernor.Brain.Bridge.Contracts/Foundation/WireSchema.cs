using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>What a wire field holds.</summary>
internal enum WireKind
{
    Scalar,
    Enum,
    Message,
    Repeated,
    Map,
}

/// <summary>
/// One field of a wire record in protobuf terms, for the few validators that walk messages generically (the colony
/// row split and join, the clock enum walk, the "only these fields" checks). A oneof choice contributes one field per
/// case, named after the case in snake_case, so a record reads like the message it replaced.
/// </summary>
internal sealed class WireField
{
    private readonly Func<object, object?> _get;

    internal WireField(string name, PropertyInfo property, WireKind kind, Type valueType, Type? caseType)
    {
        Name = name;
        Property = property;
        Kind = kind;
        ValueType = valueType;
        CaseType = caseType;
        var value = caseType?.GetProperty("Value");
        _get = caseType is null ? property.GetValue! : owner => property.GetValue(owner) is { } choice && caseType.IsInstanceOfType(choice) ? value!.GetValue(choice) : null;
    }

    /// <summary>The proto field name (snake_case).</summary>
    public string Name { get; }

    public PropertyInfo Property { get; }

    public WireKind Kind { get; }

    /// <summary>The message type, the enum type, the element type of a repeated field, or the scalar type.</summary>
    public Type ValueType { get; }

    /// <summary>The oneof case record this field is, when it is one.</summary>
    public Type? CaseType { get; }

    public bool IsOneofCase => CaseType is not null;

    public bool IsRepeated => Kind == WireKind.Repeated;

    public bool IsMap => Kind == WireKind.Map;

    /// <summary>The field's value on <paramref name="owner"/>: null when unset, a boxed scalar, record, or array.</summary>
    public object? Get(object owner) => _get(owner);

    /// <summary>
    /// Sets the field on a record that is still being built. The wire records are immutable to every reader; this runs
    /// only on instances <see cref="WireMessage.New"/> just made, before they are shared.
    /// </summary>
    public void Set(object owner, object? value)
    {
        Property.SetValue(owner, CaseType is null ? value : Activator.CreateInstance(CaseType, value));
    }

    /// <summary>Whether the field is populated: set, or a non-empty repeated field or map.</summary>
    public bool IsPopulated(object owner)
    {
        var value = Get(owner);
        return value switch
        {
            null => false,
            IDictionary dictionary => dictionary.Count > 0,
            IEnumerable and not string and not byte[] when Kind == WireKind.Repeated => ((IEnumerable)value).GetEnumerator().MoveNext(),
            _ => true,
        };
    }

    /// <summary>The elements of a repeated field.</summary>
    public IEnumerable<object?> Elements(object owner) => Get(owner) is IEnumerable items ? items.Cast<object?>() : [];

    /// <summary>The repeated field's value for <paramref name="items"/> (an <c>EquatableArray&lt;T&gt;</c>).</summary>
    public object MakeArray(IEnumerable<object?> items)
    {
        var from = typeof(EquatableArray).GetMethod(nameof(EquatableArray.From))!.MakeGenericMethod(ValueType);
        var typed = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ValueType))!;
        foreach (var item in items)
        {
            typed.Add(item);
        }
        return from.Invoke(null, [typed])!;
    }
}

/// <summary>The fields of one wire record type, in declaration order (the proto field order).</summary>
internal sealed class WireMessage
{
    private static readonly ConcurrentDictionary<Type, WireMessage> Cache = new();

    private readonly Dictionary<string, WireField> _byName;

    private WireMessage(Type type, List<WireField> fields)
    {
        Type = type;
        Fields = fields;
        _byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    public Type Type { get; }

    public IReadOnlyList<WireField> Fields { get; }

    public WireField? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>A new, empty record of this type.</summary>
    public object New() => Activator.CreateInstance(Type)!;

    /// <summary>The populated fields of <paramref name="message"/> with their values, in declaration order.</summary>
    public static IEnumerable<(WireField Field, object Value)> PopulatedFields(object message)
    {
        foreach (var field in Of(message).Fields)
        {
            if (field.Get(message) is { } value && field.IsPopulated(message))
            {
                yield return (field, value);
            }
        }
    }

    public static WireMessage Of(Type type) => Cache.GetOrAdd(type, Build);

    public static WireMessage Of(object message) => Of(message.GetType());

    /// <summary>Whether <paramref name="type"/> is one of the wire records (not a scalar, enum, array or collection).</summary>
    public static bool IsRecord(Type type) => type.IsClass && type.Namespace is { } ns && ns.StartsWith("RimGovernor.Contracts.Wire", StringComparison.Ordinal) && !type.IsAbstract;

    /// <summary>The proto name of a record property or case: <c>ShortCircuitTick</c> is <c>short_circuit_tick</c>.</summary>
    public static string SnakeCase(string pascal)
    {
        var text = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(pascal[i - 1]) || char.IsDigit(pascal[i - 1])))
                {
                    text.Append('_');
                }
                text.Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }
        return text.ToString();
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static WireKind KindOf(Type type) =>
        type.IsEnum ? WireKind.Enum
        : IsRecord(type) ? WireKind.Message
        : WireKind.Scalar;

    private static WireMessage Build(Type type)
    {
        var fields = new List<WireField>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod is null)
            {
                continue;
            }
            var propertyType = Unwrap(property.PropertyType);
            if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(EquatableArray<>))
            {
                var element = propertyType.GetGenericArguments()[0];
                fields.Add(new WireField(SnakeCase(property.Name), property, WireKind.Repeated, element, null));
            }
            else if (propertyType.IsGenericType && propertyType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
            {
                fields.Add(new WireField(SnakeCase(property.Name), property, WireKind.Map, propertyType.GetGenericArguments()[^1], null));
            }
            else if (propertyType.IsAbstract && propertyType.IsClass && propertyType.GetNestedTypes().Where(n => n.Name.EndsWith("Case", StringComparison.Ordinal) && n.BaseType == propertyType).ToList() is { Count: > 0 } cases)
            {
                foreach (var caseType in cases)
                {
                    var payload = caseType.GetProperty("Value")!.PropertyType;
                    fields.Add(new WireField(SnakeCase(caseType.Name[..^4]), property, KindOf(Unwrap(payload)), Unwrap(payload), caseType));
                }
            }
            else
            {
                fields.Add(new WireField(SnakeCase(property.Name), property, KindOf(propertyType), propertyType, null));
            }
        }
        return new WireMessage(type, fields);
    }
}
