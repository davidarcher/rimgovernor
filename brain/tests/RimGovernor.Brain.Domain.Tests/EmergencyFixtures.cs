using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared emergency census pieces: a whole status read, a placed pawn-table row the census joins, and whole
/// Policy census rows a test then changes with <c>with</c>.
/// </summary>
internal static class EmergencyFixtures
{
    public static readonly RimGovernor.Brain.GameFacts.Common.Identity Identity = new() { ColonyId = "colony", MapId = 1, LoadToken = "load" };

    /// <summary>A status read at tick 100 listing the colonists, no dependents and no threats.</summary>
    public static RimGovernor.Brain.GameFacts.Observations.StatusSnapshot Status(params string[] colonists) => new()
    {
        Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = Identity, Tick = 100 },
        Threats = new() { Pawns = [], HostileBuildings = [] },
        Colonists = [.. colonists.Select(id => new RimGovernor.Brain.GameFacts.Common.Ref { Id = id })],
        DependentPatients = new() { Patients = [] },
    };

    /// <summary><see cref="PawnFixtures.Row"/> standing on map 1 at (x, z), so a census reference resolves to it.</summary>
    public static RimGovernor.Brain.GameFacts.Observations.PawnState Placed(string id, int x = 0, int z = 0)
    {
        var row = PawnFixtures.Row(id);
        return row with { Pawn = row.Pawn with { Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, new RimGovernor.Brain.Domain.Cell { X = x, Z = z }) } };
    }

    /// <summary>A live, standing, healthy, fed colonist out of bed at (10, 10) in no mental state.</summary>
    public static EmergencyPawn Colonist(string id)
    {
        var row = Placed(id, 10, 10);
        return new() { Observed = row with { Needs = row.Needs with { Food = new RimGovernor.Brain.GameFacts.Observations.FoodNeed.Has(1, RimGovernor.Brain.GameFacts.Defs.HungerCategory.Fed) } } };
    }

    /// <summary>A live, standing, discovered, engaging humanlike threat at (20, 20), 12 cells from a colonist.</summary>
    public static EmergencyThreat Threat(string id, ThreatKind kind = ThreatKind.Hostile) =>
        new EmergencyThreat.PawnThreat(Placed(id, 20, 20), ThreatPawn(id), kind);

    public static RimGovernor.Brain.GameFacts.Observations.ThreatPawn ThreatPawn(string id) => new()
    {
        Pawn = new RimGovernor.Brain.GameFacts.Common.Ref { Id = id },
        Ours = false,
        Passive = false,
        NearestColonist = new RimGovernor.Brain.GameFacts.Observations.ColonistDistance.Nearest(12),
        FactionHostile = true,
        PrisonBreak = false,
        PredatorHunt = false,
        Predator = false,
        Downed = false,
    };

    public static RimGovernor.Brain.GameFacts.Observations.ThreatBuilding ThreatBuilding(string id) => new()
    {
        Building = new RimGovernor.Brain.GameFacts.Observations.EntityRef { Id = id, DefName = "Hive", Label = id, Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, new(20, 20)) },
        Snapshot = "building-token",
        NearestColonist = new RimGovernor.Brain.GameFacts.Observations.ColonistDistance.Nearest(12),
        Passive = false,
        Fogged = false,
        Occupied = new RimGovernor.Brain.GameFacts.Observations.Rectangle { Minimum = new(20, 20), Maximum = new(21, 21) },
    };
}
