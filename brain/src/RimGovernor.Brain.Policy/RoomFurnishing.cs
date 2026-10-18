namespace RimGovernor.Brain.Policy;

/// <summary>
/// One entry of a room role's furniture: <see cref="Count"/> pieces, each any one of <see cref="Things"/>,
/// planned in the role's template from <see cref="Piece"/>. A non-empty <see cref="Key"/> names the template
/// slot group the entry fills (the part of a slot before its dot: "bed" for "bed.2"); an empty one fills any
/// slot of <see cref="Piece"/>'s definition.
/// </summary>
public sealed record FurnishingWant
{
    public string Key { get; init; } = string.Empty;

    public IReadOnlyList<string> Things { get; init; } = [];

    public int Count { get; init; }

    public InteriorPieceDef Piece { get; init; } = new();
}

/// <summary>A room's desired furniture: the listed pieces, and whether any is yet to be built.</summary>
public sealed record Furnishing
{
    public IReadOnlyList<WantedPiece> Pieces { get; init; } = [];

    /// <summary>True while a listed piece is a template slot with nothing standing in it.</summary>
    public bool Absent { get; init; }
}

public static class RoomFurnishingRules
{
    /// <summary>
    /// The room's desired furniture list, derived from its role (<paramref name="interior"/>'s template) and the
    /// buildings standing now. Each want lists the pieces of its things already standing inside the room where
    /// they stand (so a piece a quality lever added stays listed and the list never repacks it), then fills the
    /// shortfall from the role's template slots no building covers, in template order. The same input gives the
    /// same list, and building a listed piece changes only that entry.
    /// </summary>
    public static Furnishing RoomFurnishing(PlannedRoom room, InteriorRoom interior, IReadOnlyList<FurnishingWant> wants, IReadOnlyList<CurrentBuilding> built)
    {
        var taken = new HashSet<Cell>();
        foreach (var b in built)
        {
            foreach (var c in b.Cells.Items)
            {
                taken.Add(c);
            }
        }

        var bounds = room.Region.Bounds();
        var plans = new Dictionary<InteriorPieceDef, InteriorPlan?>();
        var pieces = new List<WantedPiece>();
        var absent = false;
        foreach (var w in wants)
        {
            var count = 0;
            foreach (var b in built)
            {
                if (count < w.Count && b.Cells.Count > 0 && FurnitureRoomsRules.RectInside(bounds, PlacementSearchRules.CellsRectangle(b.Cells.Items)) && w.Things.Contains(b.Building!.Definition))
                {
                    var r = PlacementSearchRules.CellsRectangle(b.Cells.Items);
                    pieces.Add(
                        new WantedPiece
                        {
                            DefName = b.Building.Definition,
                            Minimum = new Cell(r.X, r.Z),
                            Maximum = new Cell(r.X + r.Width - 1, r.Z + r.Height - 1),
                        }
                    );
                    count++;
                }
            }

            if (count >= w.Count)
            {
                continue;
            }

            if (!plans.TryGetValue(w.Piece, out var plan))
            {
                var (planned, ok) = InteriorRegionRules.PlanRoomInterior(interior, w.Piece);
                plan = ok ? planned : null;
                plans[w.Piece] = plan;
            }

            if (plan is null)
            {
                continue;
            }

            foreach (var slot in plan.Pieces)
            {
                if (count >= w.Count || !Fills(w, slot) || PlannedRules.RectCells(slot.Rect).Any(taken.Contains))
                {
                    continue;
                }

                foreach (var c in PlannedRules.RectCells(slot.Rect))
                {
                    taken.Add(c);
                }

                pieces.Add(slot.Wanted());
                absent = true;
                count++;
            }
        }

        return new Furnishing { Pieces = pieces, Absent = absent };
    }

    private static bool Fills(FurnishingWant w, InteriorPiece slot)
    {
        if (w.Key.Length == 0)
        {
            return slot.Def == w.Piece.Def;
        }

        var dot = slot.Slot.IndexOf('.', StringComparison.Ordinal);
        return (dot < 0 ? slot.Slot : slot.Slot[..dot]) == w.Key;
    }
}
