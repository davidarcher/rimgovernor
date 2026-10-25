
namespace RimGovernor.Brain.Domain.Tests;

internal static class PowerBuildingFixtures
{
    internal static RimGovernor.Brain.GameFacts.Observations.BuildingState Trader(string id, string definition, Cell cell, double rated, double output, bool powered, string network = "net") => new()
    {
        Building = new RimGovernor.Brain.GameFacts.Observations.EntityRef { Id = id, DefName = definition, Label = definition, Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(0, new RimGovernor.Brain.Domain.Cell { X = cell.X, Z = cell.Z }) },
        Rotation = RimGovernor.Brain.GameFacts.Placement.Rotation.North, Stuff = null, Faction = null,
        Status = RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built, HitPoints = new RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints.Untracked(), Construction = null,
        Service = BuildingServiceWire.NoService with { Power = new RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader { RatedW = rated, OutputW = output, PowerOn = powered, Network = network } },
        Settings = new RimGovernor.Brain.GameFacts.Observations.BuildingSettings { Forbidden = false, Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.None() },
        Burning = false, Roofed = false, RainVulnerable = false, FuelDefinitions = [], Turret = null, Conduit = RimGovernor.Brain.GameFacts.Observations.ConduitKind.None,
        Occupied = new RimGovernor.Brain.GameFacts.Observations.Rectangle { Minimum = new RimGovernor.Brain.Domain.Cell { X = cell.X, Z = cell.Z }, Maximum = new RimGovernor.Brain.Domain.Cell { X = cell.X, Z = cell.Z } },
        Anomaly = null, Door = null,
    };
}