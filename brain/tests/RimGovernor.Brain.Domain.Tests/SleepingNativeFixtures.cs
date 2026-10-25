using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Complete native sleeping rows for policy scenarios.</summary>
internal static class SleepingNativeFixtures
{
    internal static RoomQualityStatKeys StatKeys => SleepingUpkeepReads.QualityStatKeys;
    internal static RimGovernor.Brain.GameFacts.Observations.UpkeepPerson Person(PawnId id, EquatableArray<PawnId> partners = default, bool bedSharingAllowed = false) => new()
    {
        Pawn = id, Partners = partners, BedSharingAllowed = bedSharingAllowed, Title = new RimGovernor.Brain.GameFacts.Observations.RoyalStanding.Untitled(),
    };

    internal static RimGovernor.Brain.GameFacts.Observations.PawnState Pawn(PawnId id, string? ownedBed = null, double comfortableMin = 0, double comfortableMax = 0) =>
        PawnFixtures.Row(id.Value) with
        {
            OwnedBed = string.IsNullOrEmpty(ownedBed) ? null : new RimGovernor.Brain.GameFacts.Common.Ref { Id = ownedBed },
            Settings = PawnFixtures.Row(id.Value).Settings with
            {
                Body = PawnFixtures.Body with { ComfortableMinC = comfortableMin, ComfortableMaxC = comfortableMax },
            },
        };

    internal static RimGovernor.Brain.GameFacts.Observations.UpkeepBed Bed(string id, bool medical = false, bool prisoners = false,
        bool slaves = false, double temperature = 0, EquatableArray<PawnId> owners = default,
        EquatableArray<PawnId> users = default, EquatableArray<PawnId> accessibleTo = default, string room = "",
        RimGovernor.Brain.GameFacts.Observations.BedQuality? quality = null) => new()
    {
        Id = id, Medical = medical, Prisoners = prisoners, ForSlaves = slaves,
        TemperatureC = temperature, Owners = owners, Users = users, AccessibleTo = accessibleTo, Room = room,
        Quality = quality ?? new RimGovernor.Brain.GameFacts.Observations.BedQuality.Ungraded(),
    };

    internal static RimGovernor.Brain.GameFacts.Observations.BuildingState Building(string id, Resource? definition = null, Cell cell = default, string? stuff = null, bool roofed = false) => new()
    {
        Roofed = roofed, RainVulnerable = false, FuelDefinitions = [], Turret = null, Conduit = RimGovernor.Brain.GameFacts.Observations.ConduitKind.None,
        Building = new RimGovernor.Brain.GameFacts.Observations.EntityRef { Id = id, DefName = definition?.Value ?? "Bed", Label = "bed", Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(0, cell) },
        Rotation = RimGovernor.Brain.GameFacts.Placement.Rotation.North, Stuff = stuff, Faction = null,
        Status = RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Built, HitPoints = new RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints.Untracked(), Construction = null,
        Service = BuildingServiceWire.NoService,
        Settings = new RimGovernor.Brain.GameFacts.Observations.BuildingSettings { Forbidden = false, Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.None() },
        Burning = false, Occupied = new RimGovernor.Brain.GameFacts.Observations.Rectangle { Minimum = cell, Maximum = cell }, Anomaly = null, Door = null,
    };

    internal static RimGovernor.Brain.GameFacts.Observations.RoomState Room(string id, RoomRole role, RoomQuality quality, int cells) => RoomFixtures.State(id, role,
        cellCount: checked((uint)cells), stats:
        [
            new() { DefName = SleepingUpkeepReads.QualityStatKeys.Space, Value = quality.Space },
            new() { DefName = SleepingUpkeepReads.QualityStatKeys.Beauty, Value = quality.Beauty },
            new() { DefName = SleepingUpkeepReads.QualityStatKeys.Cleanliness, Value = quality.Cleanliness },
            new() { DefName = SleepingUpkeepReads.QualityStatKeys.Wealth, Value = quality.Wealth },
            new() { DefName = SleepingUpkeepReads.QualityStatKeys.Impressiveness, Value = quality.Impressiveness },
        ]);
}
