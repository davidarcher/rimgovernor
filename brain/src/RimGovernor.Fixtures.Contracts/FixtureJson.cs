#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RimGovernor.Fixtures.Contracts;

/// <summary>The one JSON entry point for the fixture contracts, on both sides of <c>test/stage</c>: source-generated, never reflection.</summary>
public static class FixtureJson
{
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Info<T>());

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize(json, Info<T>()) ?? throw new JsonException("The body is JSON null.");

    private static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)FixtureJsonContext.Default.GetTypeInfo(typeof(T))!;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StageSpec))]
[JsonSerializable(typeof(StageReply))]
internal sealed partial class FixtureJsonContext : JsonSerializerContext;
