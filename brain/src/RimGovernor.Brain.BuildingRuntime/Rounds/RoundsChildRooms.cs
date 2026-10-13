using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsSleepingUpkeepPlanner
{
    internal RoundsBuildingResult StageChildRoom(CancellationToken call, CancellationToken epoch, PackedStock stock, ControlState state, RoundsReview review, StandardState goal, RoundsReading reading, ChildRoomStep step)
    {
        var bounds = step.Room.Region.Bounds();
        return Building.ReconcileRoom(call, epoch, state, review, goal, reading, stock, new RoomReconcile
        {
            Room = step.Room,
            Template = step.Template,
            Name = string.Create(CultureInfo.InvariantCulture, $"child-room-{bounds.X}-{bounds.Z}"),
            Reason = step.Need.Role.Value,
        });
    }
}

public sealed partial class Rounder
{
    /// <summary>Adds the worship and containment definitions the child rooms need to the reading's catalog.</summary>
    internal void ReviewChildRooms(RoundsReading reading)
    {
        var worship = WorshipDefinitions(reading.Projection);
        reading.Projection.AddDefinitions(reading.Frame, [.. worship, .. ContainmentDefinitions(reading.Projection)]);
        Census.RememberWorship(worship);
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static IReadOnlyList<string> WorshipDefinitions(ColonyProjection facts)
    {
        if (!facts.Facts.Ideology.TryGetValue(out var ideology))
        {
            return [];
        }
        return ideology.RequiredBuildings();
    }

    internal static IReadOnlyList<string> ContainmentDefinitions(ColonyProjection facts)
    {
        if (facts.Facts.Containment.Defs.TryGetValue(out var defs))
        {
            var output = new List<string> { defs.Holder, PolicyApi.ContainmentLampDefinition };
            if (defs.Floor.Def != "")
            {
                output.Add(defs.Floor.Def);
            }
            return output;
        }
        return [];
    }

    internal static (ChildRoomNeed Need, ContainmentVerdict Verdict) ContainmentCellNeed(ColonyProjection facts) =>
        PolicyApi.ContainmentCellNeed(facts.Facts.Containment, FurnitureDefinitions(facts), FlooringFacts(facts));

    internal static (ChildRoomNeed Need, bool Owed) IsolationRoomNeed(ColonyProjection facts) =>
        PolicyApi.IsolationRoomNeed(facts.Isolation, FurnitureDefinitions(facts));

    internal static List<ChildRoomNeed> ChildRoomNeeds(ColonyProjection facts)
    {
        var needs = new List<ChildRoomNeed>();
        if (facts.WorkPawns.TryGetValue(out var pawns))
        {
            needs.AddRange(PolicyApi.ChildRoomNeeds(pawns));
        }
        if (facts.Facts.Ideology.TryGetValue(out var ideology))
        {
            var (need, owed) = PolicyApi.WorshipRoomNeed(ideology);
            if (owed)
            {
                needs.Add(need);
            }
        }
        var (cell, verdict) = ContainmentCellNeed(facts);
        if (verdict.Owed)
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
        var (ground, gk) = ColonyGround(facts);
        if (!pk || !rk || !ck || !census!.Colony || !gk)
        {
            return new ChildRoomStep();
        }
        return PolicyApi.NextChildRoomStep(plan!, rooms!, plan!.GroundWithRock(ground, NaturalRock(facts)), census.Buildings, ChildRoomNeeds(facts), FurnitureDefinitions(facts));
    }
}
