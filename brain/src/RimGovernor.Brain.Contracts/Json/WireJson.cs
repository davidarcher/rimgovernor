using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace RimGovernor.Contracts.Wire;

/// <summary>The one JSON entry point for the wire records: source-generated, never reflection.</summary>
public static class WireJson
{
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Info<T>());

    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Info<T>());

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize(json, Info<T>()) ?? throw new JsonException("The body is JSON null.");

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize(utf8, Info<T>()) ?? throw new JsonException("The body is JSON null.");

    public static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)WireJsonContext.Default.GetTypeInfo(typeof(T))!;
}
