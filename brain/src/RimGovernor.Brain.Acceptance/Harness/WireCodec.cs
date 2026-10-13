using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using RimGovernor.Contracts.Wire;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The one translation between what the cases write and read and what the in-process host's Contracts records put on the
/// wire. The cases keep the flat oneof view the Go harness used: a oneof is the member named for its case on the message
/// that holds it (<c>{"setMode":{...}}</c>, a reply's <c>{"pending":{...}}</c>, a status's <c>{"active":{...}}</c>). The
/// host encodes the same choice as <c>{"operation":{"type":"setMode","value":{...}}}</c> (a record per case, discriminated by
/// <c>type</c>). The codec walks the Contracts type graph (the source-generated <see cref="WireJsonContext"/> metadata, never
/// a name guess) and converts either way at every depth, so no case builds or unwraps a host shape itself.
/// 64-bit counters and ids are JSON numbers on the wire in both directions; the codec never converts a number to or from a
/// string, and the host rejects a request that sends one.
/// </summary>
public static class WireCodec
{
    private static readonly ConcurrentDictionary<Type, Shape?> Shapes = new();

    /// <summary>The wire body of <paramref name="method"/>'s request from the cases' flat view; null is the JSON literal <c>null</c>.</summary>
    public static string EncodeRequest(string method, JsonNode? view)
    {
        if (view is null)
        {
            return "null";
        }

        return ToHost(view, Types(method).Request)?.ToJsonString() ?? "null";
    }

    /// <summary>
    /// Why the host's Contracts record would refuse <paramref name="encoded"/>, the request <see cref="EncodeRequest"/> produced:
    /// the deserializer's message with the JSON path, or null when the request fits the record. The host's own refusal
    /// says only that the request is not valid JSON for the method.
    /// </summary>
    public static string? Diagnose(string method, string encoded)
    {
        try
        {
            _ = JsonSerializer.Deserialize(encoded, WireJsonContext.Default.GetTypeInfo(Types(method).Request)!);
            return null;
        }
        catch (JsonException e)
        {
            return e.Message;
        }
    }

    /// <summary>The cases' flat view of a reply payload the host produced for <paramref name="method"/>.</summary>
    public static JsonObject DecodeReply(string method, string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (JsonNode.Parse(payload) is not JsonObject host)
        {
            throw new JsonException($"{method}: the reply is not a JSON object");
        }

        return (JsonObject)ToView(host, Types(method).Reply)!;
    }

    /// <summary>Like <see cref="DecodeReply"/> for a tool that may not be a wire method: null when it is not one.</summary>
    public static JsonObject? TryDecodeReply(string method, string payload) =>
        WireMethods.TryGet(method, out _) ? DecodeReply(method, payload) : null;

    /// <summary>The Contracts record a flat-view message of type <typeparamref name="T"/> denotes; throws <see cref="JsonException"/> when it does not fit.</summary>
    public static T ToRecord<T>(JsonNode view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return WireJson.Deserialize<T>(ToHost(view, typeof(T))!.ToJsonString());
    }

    /// <summary>The flat view of the host JSON of a <paramref name="type"/> message.</summary>
    public static JsonNode? ToView(JsonNode? host, Type type) => Convert(host, type, toHost: false);

    /// <summary>The host JSON of the flat view of a <paramref name="type"/> message.</summary>
    public static JsonNode? ToHost(JsonNode? view, Type type) => Convert(view, type, toHost: true);

    private static (Type Request, Type Reply) Types(string method) =>
        WireMethods.TryGet(method, out var types)
            ? types
            : throw new AcceptanceException($"{method}: no wire contract is registered for this method in WireMethods");

    private static JsonNode? Convert(JsonNode? node, Type type, bool toHost)
    {
        if (node is null)
        {
            return null;
        }

        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type == typeof(byte[]) || type.IsPrimitive || type.IsEnum)
        {
            return node.DeepClone();
        }

        if (ValueType(type, typeof(IReadOnlyDictionary<,>), 1) is { } mapValue)
        {
            if (node is not JsonObject map)
            {
                return node.DeepClone();
            }

            var copy = new JsonObject();
            foreach (var (key, value) in map)
            {
                copy[key] = Convert(value, mapValue, toHost);
            }

            return copy;
        }

        if (ValueType(type, typeof(IEnumerable<>), 0) is { } element)
        {
            if (node is not JsonArray array)
            {
                return node.DeepClone();
            }

            var copy = new JsonArray();
            foreach (var item in array)
            {
                copy.Add(Convert(item, element, toHost));
            }

            return copy;
        }

        if (node is not JsonObject obj || ShapeOf(type) is not { } shape)
        {
            return node.DeepClone();
        }

        return toHost ? ObjectToHost(obj, type, shape) : ObjectToView(obj, type, shape);
    }

    private static JsonObject ObjectToView(JsonObject obj, Type type, Shape shape)
    {
        var view = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (!shape.ByName.TryGetValue(key, out var property))
            {
                view[key] = value?.DeepClone();
                continue;
            }

            if (property.Choice is not { } choice)
            {
                view[key] = Convert(value, property.Type, toHost: false);
                continue;
            }

            if (value is not JsonObject cased || cased[choice.Discriminator] is not JsonValue tag || !tag.TryGetValue<string>(out var name) || !choice.Cases.TryGetValue(name, out var caseType))
            {
                throw new JsonException($"{type.Name}.{key}: not a {{\"{choice.Discriminator}\":<case>,\"value\":...}} choice of [{string.Join(' ', choice.Cases.Keys)}]: {value?.ToJsonString()}");
            }

            view[name] = Convert(cased["value"], caseType, toHost: false) ?? new JsonObject();
        }

        return view;
    }

    private static JsonObject ObjectToHost(JsonObject view, Type type, Shape shape)
    {
        var host = new JsonObject();
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in shape.Properties)
        {
            if (property.Choice is not { } choice)
            {
                continue;
            }

            if (view.ContainsKey(property.Name))
            {
                throw new AcceptanceException($"{type.Name}: \"{property.Name}\" is the host's encoding of a choice; write the chosen case as a member instead, one of [{string.Join(' ', choice.Cases.Keys)}]");
            }

            var set = choice.Cases.Keys.Where(name => view[name] is not null).ToList();
            if (set.Count > 1)
            {
                throw new AcceptanceException($"{type.Name}.{property.Name}: more than one case is set [{string.Join(' ', set)}]");
            }

            foreach (var name in choice.Cases.Keys)
            {
                _ = consumed.Add(name);
            }

            if (set.Count == 1)
            {
                host[property.Name] = new JsonObject
                {
                    [choice.Discriminator] = set[0],
                    ["value"] = Convert(view[set[0]], choice.Cases[set[0]], toHost: true),
                };
            }
        }

        foreach (var (key, value) in view)
        {
            if (consumed.Contains(key))
            {
                continue;
            }

            host[key] = shape.ByName.TryGetValue(key, out var property)
                ? Convert(value, property.Type, toHost: true)
                : value?.DeepClone();
        }

        return host;
    }

    // The element or value type of a collection: IEnumerable<T> -> T, IReadOnlyDictionary<K, V> -> V.
    private static Type? ValueType(Type type, Type definition, int argument)
    {
        if (type == typeof(string))
        {
            return null;
        }

        var candidates = type.GetInterfaces().ToList();
        if (type.IsInterface)
        {
            candidates.Add(type);
        }

        foreach (var candidate in candidates)
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == definition)
            {
                return candidate.GetGenericArguments()[argument];
            }
        }

        return null;
    }

    private static Shape? ShapeOf(Type type) => Shapes.GetOrAdd(type, static t =>
    {
        var info = WireJsonContext.Default.GetTypeInfo(t);
        if (info is null || info.Kind != JsonTypeInfoKind.Object || info.PolymorphismOptions is not null)
        {
            return null;
        }

        var properties = new List<Property>();
        foreach (var property in info.Properties)
        {
            var choice = ChoiceOf(property.PropertyType);
            properties.Add(new Property(property.Name, property.PropertyType, choice));
        }

        return new Shape(properties, properties.ToDictionary(p => p.Name, StringComparer.Ordinal));
    });

    private static Choice? ChoiceOf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (WireJsonContext.Default.GetTypeInfo(type)?.PolymorphismOptions is not { } options)
        {
            return null;
        }

        var cases = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var derived in options.DerivedTypes)
        {
            var name = derived.TypeDiscriminator as string ?? throw new InvalidOperationException($"{type.Name}: {derived.DerivedType.Name} has no string discriminator");
            cases[name] = derived.DerivedType.GetProperty("Value")?.PropertyType ?? throw new InvalidOperationException($"{derived.DerivedType.Name} has no Value");
        }

        return new Choice(options.TypeDiscriminatorPropertyName ?? "$type", cases);
    }

    private sealed record Shape(IReadOnlyList<Property> Properties, IReadOnlyDictionary<string, Property> ByName);

    private sealed record Property(string Name, Type Type, Choice? Choice);

    private sealed record Choice(string Discriminator, IReadOnlyDictionary<string, Type> Cases);
}
