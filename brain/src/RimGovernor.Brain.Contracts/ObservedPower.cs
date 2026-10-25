using RimGovernor.Brain.Domain;
using System.Text.Json.Serialization;
namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>The native gun's damage per second from its verb and burst timing.</summary>
public sealed record TurretGun(double Dps);

[JsonConverter(typeof(WireEnumConverter<ConduitKind>))]
public enum ConduitKind
{
    [WireName("none")] None,
    [WireName("protected")] Protected,
    [WireName("exposed")] Exposed,
}
/// <summary>The native refuelable comp's current difficulty-adjusted burn parameters.</summary>
public sealed record FuelBurn
{
    public required double PerDay { get; init; }
    public required double UnitsPerItem { get; init; }
    public required bool UseDriven { get; init; }
    public required bool InRain { get; init; }
    public required bool WhenPowered { get; init; }
    public required bool Flickable { get; init; }
}