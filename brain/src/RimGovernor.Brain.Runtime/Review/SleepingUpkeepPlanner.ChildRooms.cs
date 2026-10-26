using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed partial class SleepingUpkeepPlanner
{
    internal async Task<BuildingResult> StageChildRoomAsync(PackedStock stock, ControlState state, ReviewStep review, Sto.NeedOwner goal, ReviewReading reading, ChildRoomStep step, CancellationToken call, CancellationToken epoch)
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

public sealed partial class ColonyReview
{
    /// <summary>Adds the worship and containment definitions the child rooms need to the reading's catalog.</summary>
    internal ReviewReading ReviewChildRooms(ReviewReading reading)
    {
        var worship = SleepingUpkeepPlanner.WorshipDefinitions(reading.Reading);
        // The worship floor's terrains (a presence demand's terrain tags, #3808) join so the flooring facts can choose among them.
        var floors = reading.Catalog?.TerrainsWithTags([.. reading.Reading.Facts.Worship.FloorTags]) ?? [];
        var projection = ColonyPlanningReads.AddDefinitions(reading.Reading, reading.Frame, reading.Catalog, [.. worship, .. floors, .. SleepingUpkeepPlanner.ContainmentDefinitions(reading.Reading)]);
        Census.RememberWorship(worship);
        return reading.WithReading(projection);
    }
}

partial class SleepingUpkeepPlanner
{
    /// <summary>The worship room's furniture definitions: its required buildings and the things its presence demands count.</summary>
    internal static IReadOnlyList<string> WorshipDefinitions(ColonyReading facts) =>
        SleepingPlanningRules.WorshipDefinitions(facts.Facts);

    internal static IReadOnlyList<string> ContainmentDefinitions(ColonyReading facts) =>
        SleepingPlanningRules.ContainmentDefinitions(facts.Facts);

    internal static (ChildRoomNeed Need, bool Owed) IsolationRoomNeed(ColonyReading facts) =>
        SleepingPlanningRules.IsolationRoomNeed(facts.Facts);

    internal static List<ChildRoomNeed> ChildRoomNeeds(ColonyReading facts) =>
        SleepingPlanningRules.ChildRoomNeeds(facts.Facts, facts.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray());

    /// <summary>The deathrest chamber's furniture the deathrest chamber credit reads (#3810), resolved as the staging resolves it.</summary>
    internal static EquatableArray<DeathrestPiece> DeathrestFurnitureOf(ColonyReading facts) =>
        ChildRoomsRules.DeathrestFurniture(ChildRoomNeeds(facts), FurnitureDefinitions(facts));

    /// <summary>The planned containment cells the containment cell credit reads (#3811), predicted as the cell need predicts them.</summary>
    internal static EquatableArray<PlannedContainmentCell> ContainmentCellsOf(ColonyReading facts) =>
        ForecastContainment.CellsOf(facts.Facts.Containment, BuildingPlanner.FlooringFactsOf(facts));

    internal static List<FurnitureDefinition> FurnitureDefinitions(ColonyReading facts) =>
        SleepingPlanningRules.FurnitureDefinitions(facts.Facts);

    internal static ChildRoomStep ChildRoomStep(ColonyReading facts) =>
        SleepingPlanningRules.ChildRoomStep(facts.Facts, facts.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray(), facts.Window is { } window ? window.Value.Cells : null);
}
