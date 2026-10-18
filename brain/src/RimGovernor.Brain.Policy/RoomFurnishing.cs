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

    /// <summary>The slots the role supplies itself (the hospital ward's free-floor beds); null plans them from <see cref="Piece"/>'s template.</summary>
    public IReadOnlyList<InteriorPiece>? Slots { get; init; }
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
    public static Furnishing RoomFurnishing(PlannedRoom room, InteriorRoom? interior, IReadOnlyList<FurnishingWant> wants, IReadOnlyList<CurrentBuilding> built)
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
        var listed = new HashSet<CurrentBuilding>();
        foreach (var w in wants)
        {
            var count = 0;
            foreach (var b in built)
            {
                if (count < w.Count && b.Cells.Count > 0 && !listed.Contains(b) && FurnitureRoomsRules.RectInside(bounds, PlacementSearchRules.CellsRectangle(b.Cells.Items)) && w.Things.Contains(b.Building!.Definition))
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
                    listed.Add(b);
                    count++;
                }
            }

            if (count >= w.Count)
            {
                continue;
            }

            var slots = w.Slots;
            if (slots is null && interior is not null)
            {
                if (!plans.TryGetValue(w.Piece, out var plan))
                {
                    var (planned, ok) = InteriorRegionRules.PlanRoomInterior(interior, w.Piece);
                    plan = ok ? planned : null;
                    plans[w.Piece] = plan;
                }

                slots = plan?.Pieces;
            }

            foreach (var slot in slots ?? [])
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

    /// <summary>
    /// A facility role's standing pieces as wants, from the role's own plan (the region role table, the same plan
    /// that places the room): one want per planned definition, as many as the plan holds, with no slots of its own, so
    /// the list names the pieces standing in the room and never asks for one to be built. Placement stays with the
    /// facility's Select* ladders and the generic preview search.
    /// </summary>
    public static IReadOnlyList<FurnishingWant> FacilityWants(InteriorRoom interior)
    {
        var (plan, ok) = InteriorRegionRules.PlanRoomInterior(interior, new InteriorPieceDef());
        if (!ok)
        {
            return [];
        }

        return [.. plan.Pieces.GroupBy(p => p.Def).Select(g => new FurnishingWant { Things = [g.Key], Count = g.Count(), Piece = new InteriorPieceDef { Def = g.Key }, Slots = [] })];
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
