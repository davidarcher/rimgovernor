using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Complete native sleeping rows for policy scenarios.</summary>
internal static class SleepingNativeFixtures
{
    internal static RoomQualityStatKeys StatKeys => SleepingUpkeepReads.QualityStatKeys;
    internal static O.UpkeepPerson Person(PawnId id, EquatableArray<PawnId> partners = default, bool bedSharingAllowed = false) => new()
    {
        Pawn = id, Partners = partners, BedSharingAllowed = bedSharingAllowed, Title = new O.RoyalStanding.Untitled(),
    };

    internal static O.PawnState Pawn(PawnId id, string? ownedBed = null, double comfortableMin = 0, double comfortableMax = 0) =>
        PawnFixtures.Row(id.Value) with
        {
            OwnedBed = string.IsNullOrEmpty(ownedBed) ? null : new RimGovernor.Contracts.Wire.Common.Ref { Id = ownedBed },
            Settings = PawnFixtures.Row(id.Value).Settings with
            {
                Body = PawnFixtures.Body with { ComfortableMinC = comfortableMin, ComfortableMaxC = comfortableMax },
            },
        };

    internal static O.UpkeepBed Bed(string id, bool medical = false, bool prisoners = false,
        bool slaves = false, double temperature = 0, EquatableArray<PawnId> owners = default,
        EquatableArray<PawnId> users = default, EquatableArray<PawnId> accessibleTo = default, string room = "",
        O.BedQuality? quality = null) => new()
    {
        Id = id, Medical = medical, Prisoners = prisoners, ForSlaves = slaves,
        TemperatureC = temperature, Owners = owners, Users = users, AccessibleTo = accessibleTo, Room = room,
        Quality = quality ?? new O.BedQuality.Ungraded(),
    };

    internal static O.BuildingState Building(string id, Resource? definition = null, Cell cell = default, string? stuff = null, bool roofed = false) => new()
    {
        Roofed = roofed, RainVulnerable = false, FuelDefinitions = [], Turret = null, Conduit = O.ConduitKind.None,
        Building = new O.EntityRef { Id = id, DefName = definition?.Value ?? "Bed", Label = "bed", Place = new O.EntityPlace.OnMap(0, cell) },
        Rotation = RimGovernor.Contracts.Wire.Placement.Rotation.North, Stuff = stuff, Faction = null,
        Status = O.BuildingStatus.Built, HitPoints = new O.BuildingHitPoints.Untracked(), Construction = null,
        Service = BuildingServiceWire.NoService,
        Settings = new O.BuildingSettings { Forbidden = false, Control = new O.BuildingControl.None() },
        Burning = false, Occupied = new O.Rectangle { Minimum = cell, Maximum = cell }, Anomaly = null, Door = null,
    };

    internal static O.RoomState Room(string id, RoomRole role, RoomQuality quality, int cells) => RoomFixtures.State(id, role,
        cellCount: checked((uint)cells), stats:
        [
            new() { DefName = SleepingUpkeepReads.QualityStatKeys.Space, Value = quality.Space },
            new() { DefName = SleepingUpkeepReads.QualityStatKeys.Beauty, Value = quality.Beauty },
            new() { DefName = SleepingUpkeepReads.QualityStatKeys.Cleanliness, Value = quality.Cleanliness },
            new() { DefName = SleepingUpkeepReads.QualityStatKeys.Wealth, Value = quality.Wealth },
            new() { DefName = SleepingUpkeepReads.QualityStatKeys.Impressiveness, Value = quality.Impressiveness },
        ]);
}
