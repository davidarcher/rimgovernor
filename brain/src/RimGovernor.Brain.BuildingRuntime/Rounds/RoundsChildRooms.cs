using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsSleepingUpkeepPlanner
{
    internal async Task<RoundsBuildingResult> StageChildRoomAsync(PackedStock stock, ControlState state, RoundsReview review, Sto.NeedOwner goal, RoundsReading reading, ChildRoomStep step, CancellationToken call, CancellationToken epoch)
    {
        if (step.Room is not { } room || step.Need is not { } need)
        {
            throw new ControlException("stageChildRoom: the step carries no room or need");
        }
        var bounds = room.Region.Bounds();
        return await Building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile
        {
            Room = room,
            Template = step.Template,
            // A presence demand's floor tags and forbidden buildings (#3808): the reconcile lays a tagged floor and clears what is forbidden.
            Tags = [.. need.FloorTags.Items],
            Forbidden = need.ForbiddenDefs.Count == 0 ? null : need.ForbiddenDefs.Items.Contains,
            Name = string.Create(CultureInfo.InvariantCulture, $"child-room-{bounds.X}-{bounds.Z}"),
            Reason = need.Role.Name,
        }, call, epoch).ConfigureAwait(false);
    }
}

public sealed partial class Rounder
{
    /// <summary>Adds the worship and containment definitions the child rooms need to the reading's catalog.</summary>
    internal RoundsReading ReviewChildRooms(RoundsReading reading)
    {
        var worship = RoundsSleepingUpkeepPlanner.WorshipDefinitions(reading.Projection);
        // The worship floor's terrains (a presence demand's terrain tags, #3808) join so the flooring facts can choose among them.
        var floors = reading.Catalog?.TerrainsWithTags([.. reading.Projection.Facts.Worship.FloorTags]) ?? [];
        var projection = reading.Projection.AddDefinitions(reading.Frame, reading.Catalog, [.. worship, .. floors, .. RoundsSleepingUpkeepPlanner.ContainmentDefinitions(reading.Projection)]);
        Census.RememberWorship(worship);
        return reading.WithProjection(projection);
    }
}

partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>The worship room's furniture definitions: its required buildings and the things its presence demands count.</summary>
    internal static IReadOnlyList<string> WorshipDefinitions(ColonyProjection facts) => facts.Facts.Worship.Definitions;

    internal static IReadOnlyList<string> ContainmentDefinitions(ColonyProjection facts)
    {
        if (facts.Facts.Containment.Defs.TryGetValue(out var defs))
        {
            var output = new List<string> { defs.Holder, ContainmentCellRules.ContainmentLampDefinition };
            if (defs.Floor.Def != "")
            {
                output.Add(defs.Floor.Def);
            }
            return output;
        }
        return [];
    }

    internal static ChildRoomNeed? ContainmentCellNeed(ColonyProjection facts) =>
        ContainmentCellRules.ContainmentCellNeed(facts.Facts.Containment, FurnitureDefinitions(facts), RoundsBuildingPlanner.FlooringFactsOf(facts));

    internal static (ChildRoomNeed Need, bool Owed) IsolationRoomNeed(ColonyProjection facts) =>
        IsolationRoomRules.IsolationRoomNeed(facts.Isolation, FurnitureDefinitions(facts));

    internal static List<ChildRoomNeed> ChildRoomNeeds(ColonyProjection facts)
    {
        var needs = new List<ChildRoomNeed>();
        if (facts.WorkPawns.TryGetValue(out var pawns))
        {
            needs.AddRange(ChildRoomsRules.ChildRoomNeeds(pawns));
        }
        if (facts.Facts.Worship.Need is { } worship)
        {
            needs.Add(worship);
        }
        // The rooms the player's goals name (#3496) join the rooms the pawns owe: layout sizes and places them, the sleeping step builds them.
        needs.AddRange(ChildRoomsRules.GoalRoomNeeds(facts.Facts.GoalBuild.Rooms, needs));
        if (ContainmentCellNeed(facts) is { } cell)
        {
            needs.Add(cell);
        }
        var (isolation, isolationOwed) = IsolationRoomNeed(facts);
        if (isolationOwed)
        {
            needs.Add(isolation);
        }
        return needs;
    }

    /// <summary>The deathrest chamber's furniture the deathrest chamber credit reads (#3810), resolved as the staging resolves it.</summary>
    internal static EquatableArray<DeathrestPiece> DeathrestFurnitureOf(ColonyProjection facts) =>
        ChildRoomsRules.DeathrestFurniture(ChildRoomNeeds(facts), FurnitureDefinitions(facts));

    internal static List<FurnitureDefinition> FurnitureDefinitions(ColonyProjection facts)
    {
        var names = new HashSet<string>(WorshipDefinitions(facts).Concat(ContainmentDefinitions(facts)).Concat(facts.Isolation.Beds), StringComparer.Ordinal);
        var defs = new List<FurnitureDefinition>();
        foreach (var d in facts.Definitions)
        {
            if (d.RoomRoles.Count > 0 || names.Contains(d.Name))
            {
                defs.Add(new FurnitureDefinition { Name = d.Name, Available = d.Available, Size = d.Size, Roles = d.RoomRoles });
            }
        }
        return defs;
    }

    internal static ChildRoomStep ChildRoomStep(ColonyProjection facts)
    {
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var (ground, gk) = RoundsBuildingPlanner.ColonyGround(facts);
        if (!pk || !rk || !ck || !census!.Colony || !gk)
        {
            return new ChildRoomStep();
        }
        return ChildRoomsRules.NextChildRoomStep(plan!, rooms!, plan!.GroundWithRock(ground, RoundsBuildingPlanner.NaturalRock(facts)), census.Buildings, ChildRoomNeeds(facts), FurnitureDefinitions(facts));
    }
}
