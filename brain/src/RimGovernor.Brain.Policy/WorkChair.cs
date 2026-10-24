namespace RimGovernor.Brain.Policy;

/// <summary>One work chair owed: the chair a planned room chose for a standing bench, on that bench's interaction cell.</summary>
public readonly record struct WorkChairSeat(string Chair, Cell Cell);

/// <summary>
/// The rooms/work-chairs Need's review (#3039): the tiler prices a work chair for a bench definition
/// (<see cref="PlannedRoom.WorkChairs"/>), and once such a bench stands in its planned room the chair goes on the
/// bench's own interaction cell, where the worker stands. A room with no chair chosen owes none.
/// </summary>
public static class WorkChairRules
{
    /// <summary>The chair definitions the plan's rooms chose, each once, in ordinal order.</summary>
    public static IReadOnlyList<string> ChairDefinitions(LayoutPlan plan) =>
        [.. plan.Rooms.SelectMany(r => r.WorkChairs.Values).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The seats owed, in plan then census order: for each standing bench inside a planned room that chose a chair for
    /// its definition, the bench's interaction cell when it lies in the room and no building, blueprint or frame stands
    /// there. A bench whose shape has no interaction cell owes none.
    /// </summary>
    public static EquatableArray<WorkChairSeat> OwedSeats(LayoutPlan plan, CurrentConstruction built, PieceShapes shapes)
    {
        var taken = new HashSet<Cell>();
        foreach (var b in built.Buildings)
        {
            taken.UnionWith(b.Cells);
        }
        foreach (var s in built.Sites)
        {
            if (s.Building is { } site)
            {
                taken.Add(site.Cell);
            }
        }
        var seats = new List<WorkChairSeat>();
        foreach (var room in plan.Rooms)
        {
            if (room.WorkChairs.Count == 0)
            {
                continue;
            }
            foreach (var b in built.Buildings)
            {
                if (b.Building is not { } bench || !room.WorkChairs.TryGetValue(bench.Definition, out var chair) || !room.Region.Contains(bench.Cell))
                {
                    continue;
                }
                var (shape, ok) = shapes.Get(bench.Definition);
                if (!ok)
                {
                    continue;
                }
                var piece = new InteriorPiece
                {
                    Def = bench.Definition,
                    Size = shape.Size,
                    Rot = bench.Rotation,
                    Rect = InteriorRules.OccupiedRect(bench.Cell, shape.Size, bench.Rotation),
                    InteractionOffset = shape.Interaction,
                };
                var (cell, at) = piece.Interaction();
                if (at && room.Region.Contains(cell) && taken.Add(cell))
                {
                    seats.Add(new WorkChairSeat(chair, cell));
                }
            }
        }
        return [.. seats];
    }
}
