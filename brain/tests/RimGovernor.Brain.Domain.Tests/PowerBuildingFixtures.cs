using O = RimGovernor.Contracts.Wire.Observations;
using C = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Domain.Tests;

internal static class PowerBuildingFixtures
{
    internal static O.BuildingState Trader(string id, string definition, Cell cell, double rated, double output, bool powered, string network = "net") => new()
    {
        Building = new O.EntityRef { Id = id, DefName = definition, Label = definition, Place = new O.EntityPlace.OnMap(0, new RimGovernor.Brain.Domain.Cell { X = cell.X, Z = cell.Z }) },
        Rotation = RimGovernor.Contracts.Wire.Placement.Rotation.North, Stuff = null, Faction = null,
        Status = O.BuildingStatus.Built, HitPoints = new O.BuildingHitPoints.Untracked(), Construction = null,
        Service = BuildingServiceWire.NoService with { Power = new O.BuildingPower.Trader { RatedW = rated, OutputW = output, PowerOn = powered, Network = network } },
        Settings = new O.BuildingSettings { Forbidden = false, Control = new O.BuildingControl.None() },
        Burning = false, Roofed = false, RainVulnerable = false, FuelDefinitions = [], Turret = null, Conduit = O.ConduitKind.None,
        Occupied = new O.Rectangle { Minimum = new RimGovernor.Brain.Domain.Cell { X = cell.X, Z = cell.Z }, Maximum = new RimGovernor.Brain.Domain.Cell { X = cell.X, Z = cell.Z } },
        Anomaly = null, Door = null,
    };
}