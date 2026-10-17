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
[JsonSerializable(typeof(GearPolicyReply))]
[JsonSerializable(typeof(RuntimeFaultReply))]
[JsonSerializable(typeof(RecoveryAreaReply))]
[JsonSerializable(typeof(MapScopeReply))]
[JsonSerializable(typeof(DeepResourcesReply))]
[JsonSerializable(typeof(FixtureDone))]
[JsonSerializable(typeof(CoordinationRead))]
[JsonSerializable(typeof(ClothingActivityRead))]
[JsonSerializable(typeof(NeedCapacityRead))]
[JsonSerializable(typeof(MaintenanceRead))]
[JsonSerializable(typeof(ServiceShelterPrepared))]
[JsonSerializable(typeof(ServiceShelterRead))]
[JsonSerializable(typeof(ProductiveCookingRead))]
[JsonSerializable(typeof(PortfolioNeedsRead))]
[JsonSerializable(typeof(ThermalRead))]
[JsonSerializable(typeof(FrozenNeedsRead))]
[JsonSerializable(typeof(SleepingPrepared))]
[JsonSerializable(typeof(ControlRead))]
[JsonSerializable(typeof(CookingRead))]
[JsonSerializable(typeof(DeliveryRead))]
internal sealed partial class FixtureJsonContext : JsonSerializerContext;
