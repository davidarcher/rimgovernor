using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Contracts.Wire;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The acceptance harness speaks the host's Contracts JSON through <see cref="WireCodec"/> (#3123). These tests pin the
/// codec to the Contracts records: a golden pair for the request that failed live, and a synthesized document for every
/// request and reply type of every wire method, with every case of every choice, that must survive host JSON ->
/// cases' flat view -> host JSON unchanged.
/// </summary>
public sealed class WireCodecTests
{
    public static TheoryData<string> Methods() => [.. WireMethods.All.Keys.Order(StringComparer.Ordinal)];

    [Fact]
    public void SetModeRequestIsTheHostsPolymorphicOperation()
    {
        var view = JsonNode.Parse("""{"setMode":{"identity":{"colonyId":"c","loadToken":"t","mapId":0},"expectedGeneration":196,"mode":"MODE_AUTO"}}""");

        var wire = WireCodec.EncodeRequest("authority_control", view);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"operation":{"type":"setMode","value":{"identity":{"colonyId":"c","loadToken":"t","mapId":0},"expectedGeneration":196,"mode":"MODE_AUTO"}}}"""),
            JsonNode.Parse(wire)));
        var typed = WireJson.Deserialize<RimGovernor.Contracts.Wire.Authority.ControlRequest>(wire);
        Assert.Equal(196UL, typed.Operation!.SetMode!.ExpectedGeneration);
        Assert.Equal(RimGovernor.Contracts.Wire.Authority.Mode.Auto, typed.Operation.SetMode.Mode);
    }

    [Fact]
    public void ReplyOutcomeAndNestedStateAreFlattened()
    {
        var granted = WireCodec.DecodeReply("authority_control", """{"outcome":{"type":"granted","value":{"authority":{"mode":"MODE_AUTO"}}}}""");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"granted":{"authority":{"mode":"MODE_AUTO"}}}"""), granted));

        var status = WireCodec.DecodeReply("authority_read_status", """{"outcome":{"type":"status","value":{"state":{"type":"inactive","value":{"reason":"REVOCATION_REASON_MANUAL"}}}}}""");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"status":{"inactive":{"reason":"REVOCATION_REASON_MANUAL"}}}"""), status));
    }

    [Fact]
    public void HostEncodingOfAChoiceIsRefusedInARequest()
    {
        var host = JsonNode.Parse("""{"operation":{"type":"setMode","value":{}}}""");
        _ = Assert.Throws<AcceptanceException>(() => WireCodec.EncodeRequest("authority_control", host));
    }

    [Fact]
    public void TwoCasesOfOneChoiceAreRefused()
    {
        var view = JsonNode.Parse("""{"setMode":{},"revoke":{}}""");
        _ = Assert.Throws<AcceptanceException>(() => WireCodec.EncodeRequest("authority_control", view));
    }

    [Fact]
    public void AMethodWithoutAContractIsAHarnessError()
    {
        _ = Assert.Throws<AcceptanceException>(() => WireCodec.EncodeRequest("no_such_method", new JsonObject()));
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public void EveryRequestAndReplyTypeRoundTrips(string method)
    {
        var (request, reply) = WireMethods.All[method];
        foreach (var type in new[] { request, reply })
        {
            var variants = Choices(type, 0, []).DefaultIfEmpty(1).Max();
            for (var variant = 0; variant < variants; variant++)
            {
                var host = Fill(type, 0, variant)!;
                var text = host.ToJsonString();
                var typed = JsonSerializer.Deserialize(text, WireJsonContext.Default.GetTypeInfo(type)!)!;

                var view = WireCodec.ToView(host, type);
                Assert.Equal(variants > 1, !JsonNode.DeepEquals(host, view));
                Assert.True(JsonNode.DeepEquals(host, WireCodec.ToHost(view, type)), $"{method} {type.Name} variant {variant}: host -> view -> host changed the document");

                var viaView = JsonSerializer.Deserialize(WireCodec.ToHost(view, type)!.ToJsonString(), WireJsonContext.Default.GetTypeInfo(type)!)!;
                Assert.Equal(JsonSerializer.Serialize(typed, WireJsonContext.Default.GetTypeInfo(type)!), JsonSerializer.Serialize(viaView, WireJsonContext.Default.GetTypeInfo(type)!));
            }
        }
    }

    // The largest case count of any choice reachable from the type: how many documents cover every case.
    private static IEnumerable<int> Choices(Type type, int depth, HashSet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (depth > 6 || !seen.Add(type))
        {
            yield break;
        }

        if (Element(type) is { } element)
        {
            foreach (var count in Choices(element, depth + 1, seen))
            {
                yield return count;
            }

            yield break;
        }

        var info = WireJsonContext.Default.GetTypeInfo(type);
        if (info?.Kind != JsonTypeInfoKind.Object)
        {
            yield break;
        }

        foreach (var property in info.Properties)
        {
            if (WireJsonContext.Default.GetTypeInfo(property.PropertyType)?.PolymorphismOptions is { } options)
            {
                yield return options.DerivedTypes.Count;
                foreach (var derived in options.DerivedTypes)
                {
                    foreach (var count in Choices(derived.DerivedType.GetProperty("Value")!.PropertyType, depth + 1, seen))
                    {
                        yield return count;
                    }
                }
            }
            else
            {
                foreach (var count in Choices(property.PropertyType, depth + 1, seen))
                {
                    yield return count;
                }
            }
        }
    }

    private static Type? Element(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[]))
        {
            return null;
        }

        return type.GetInterfaces().Append(type)
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0])
            .FirstOrDefault();
    }

    // A host-JSON document for the type with every optional member present and each choice on case (variant % cases).
    private static JsonNode? Fill(Type type, int depth, int variant)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string))
        {
            return "x";
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(byte[]))
        {
            return "AQID";
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return JsonSerializer.SerializeToNode(values.GetValue(Math.Min(1, values.Length - 1)), type, WireJsonContext.Default.Options);
        }

        if (type == typeof(double) || type == typeof(float))
        {
            return 1.5;
        }

        if (type.IsPrimitive)
        {
            return 1;
        }

        if (type.GetInterfaces().Append(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)) is { } map)
        {
            var value = depth > 6 ? null : Fill(map.GetGenericArguments()[1], depth + 1, variant);
            return value is null ? new JsonObject() : new JsonObject { ["k"] = value };
        }

        if (Element(type) is { } element)
        {
            var item = depth > 6 ? null : Fill(element, depth + 1, variant);
            return item is null ? new JsonArray() : new JsonArray(item);
        }

        var info = WireJsonContext.Default.GetTypeInfo(type) ?? throw new InvalidOperationException($"{type} is not registered");
        var obj = new JsonObject();
        if (depth > 6)
        {
            return obj;
        }

        foreach (var property in info.Properties)
        {
            if (WireJsonContext.Default.GetTypeInfo(property.PropertyType)?.PolymorphismOptions is { } options)
            {
                var derived = options.DerivedTypes[variant % options.DerivedTypes.Count];
                obj[property.Name] = new JsonObject
                {
                    [options.TypeDiscriminatorPropertyName!] = (string)derived.TypeDiscriminator!,
                    ["value"] = Fill(derived.DerivedType.GetProperty("Value")!.PropertyType, depth + 1, variant),
                };
            }
            else
            {
                // Custom observation converters encode domain facts as nullable wire values, not CLR getter objects.
                obj[property.Name] = property.CustomConverter switch
                {
                    ObservedNumberJsonConverter => JsonValue.Create(1.5),
                    ObservedMemoriesJsonConverter => new JsonArray(),
                    _ => Fill(property.PropertyType, depth + 1, variant),
                };
            }
        }

        return obj;
    }
}
