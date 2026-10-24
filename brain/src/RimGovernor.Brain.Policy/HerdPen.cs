namespace RimGovernor.Brain.Policy;

// The pen. A unit with roaming animals (RequiresPen: the races the game walks off the map unless they are fully
// enclosed) gets a pen room: a fence and a gate round grazing ground, sized by the game's pen food sum (PenCells over
// the herd's PenDemand), and one PenMarker inside it claims the enclosure for the game's pen logic. The animals sleep
// in it, on the open ground. Pen membership comes from the native enclosed-pen lookup, so a marker standing is not an
// animal contained.

/// <summary>
/// The pen a unit asks for: Race is the unit's key (the herd's race, empty for the misc unit;
/// <see cref="PlannedRoom.Herd"/>) and Cells the interior its animals' grazing needs.
/// </summary>
public sealed record PenWant(string Race, int Cells);

public static class RoundsFactsPenExtensions
{
    /// <summary>The pens the layout gives the herd, from the routine reading; none while the animal census or the pasture table is unread.</summary>
    public static IReadOnlyList<PenWant> PenWants(this RoundsFacts f)
    {
        var known = f.AnimalUpkeep.Animals.TryGetValue(out var animals);
        var pastureKnown = f.Pasture.TryGetValue(out var pasture);
        if (!known || !pastureKnown)
        {
            return [];
        }
        return HerdPlanRules.PlanHerd(f.HerdPlanInput()).PenWants(animals!, f.AnimalUpkeep.AnimalRaces, pasture!);
    }
}

public sealed partial record HerdPlan
{
    /// <summary>
    /// The pens the herd plan asks for, the misc unit's first and then each herd's in name order. A unit asks for
    /// one only for the races whose animals are held in a pen (RequiresPen), sized for the larger of the race's
    /// ceiling and the animals kept, by PenCells over the weakest grazing terrain of pasture (the pen's site is not
    /// chosen yet). A unit whose feed is unread asks for none, as does a pasture table that grows nothing.
    /// </summary>
    public IReadOnlyList<PenWant> PenWants(IReadOnlyList<UpkeepAnimal> animals, AnimalRaceCatalog races, PastureTable pasture)
    {
        var (perCell, grows) = pasture.Weakest();
        if (!grows)
        {
            return [];
        }
        var kept = new Dictionary<Resource, long>();
        foreach (var a in animals)
        {
            if (a.RequiresPen)
            {
                kept[a.Definition] = kept.GetValueOrDefault(a.Definition) + 1;
            }
        }
        var units = new Dictionary<string, Dictionary<Resource, long>>(StringComparer.Ordinal);
        foreach (var (race, n) in kept)
        {
            var unit = Herds.Contains(race) ? race.Value : "";
            if (!units.TryGetValue(unit, out var byRace))
            {
                byRace = [];
                units[unit] = byRace;
            }
            byRace[race] = Math.Max(Policy.PopulationMax.GetValueOrDefault(race), n);
        }
        var keys = units.Keys.ToList();
        keys.Sort(StringComparer.Ordinal);
        var @out = new List<PenWant>();
        foreach (var unit in keys)
        {
            var (demand, known) = PenFoodRules.PenDemand(races, units[unit]);
            if (known)
            {
                @out.Add(new PenWant(unit, PenFoodRules.PenCells(demand, perCell)));
            }
        }
        return @out;
    }
}

public sealed partial record LayoutPlan
{
    /// <summary>The interior cells of the pens of the unit keyed herd.</summary>
    internal int PenCells(string herd) => HerdRooms(HerdRoomsRules.PlannedPen, herd).Sum(r => r.Region.Bounds().Width * r.Region.Bounds().Height);
}

public static class HerdPenRules
{
    /// <summary>The def of an outdoor room's ring fence (the pen, the materials yard).</summary>
    public const string PenFenceDefinition = "Fence";

    /// <summary>The def of an outdoor room's ring gate.</summary>
    public const string PenGateDefinition = "FenceGate";

    /// <summary>The pen's one piece.</summary>
    public const string PenMarkerDefinition = "PenMarker";

    /// <summary>The near-square interior holding at least cells cells.</summary>
    internal static (int W, int H) PenSide(int cells)
    {
        var w = (int)Math.Ceiling(Math.Sqrt(Math.Max(cells, 1)));
        var h = (cells + w - 1) / w;
        return (w, Math.Max(h, 1));
    }

    /// <summary>The cells the pens still lack, summed over the wants: the replan trigger for a herd whose grazing outgrew its pens.</summary>
    public static int PensOwed(LayoutPlan plan, IReadOnlyList<PenWant> wants)
    {
        var n = 0;
        foreach (var w in wants)
        {
            n += Math.Max(w.Cells - plan.PenCells(w.Race), 0);
        }
        return n;
    }

    /// <summary>Reports a PenMarker standing in room.</summary>
    public static bool PenMarkerStanding(PlannedRoom room, IReadOnlyList<CurrentBuilding> built)
    {
        foreach (var b in built)
        {
            if (b.Building!.Definition == PenMarkerDefinition && b.Cells.Count > 0 && room.Region.Contains(b.Cells[0]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reports the plan holding at least one pen and every pen enclosed with its marker standing: the fence and
    /// gate as planned, one PenMarker inside.
    /// </summary>
    public static bool PensStand(LayoutPlan plan, GroundCensus ground, IReadOnlyList<CurrentBuilding> built)
    {
        var pens = plan.RoomsOf(HerdRoomsRules.PlannedPen);
        foreach (var room in pens)
        {
            if (!plan.GroundMatches(room, ground) || !PenMarkerStanding(room, built))
            {
                return false;
            }
        }
        return pens.Count > 0;
    }

    /// <summary>
    /// The wanted marker for pen: the one standing in it, else the free interior cell nearest its centre that the
    /// marker's whole shape fits on. Not ok when the pen has no room for it.
    /// </summary>
    internal static (WantedPiece Piece, bool Ok) PenMarkerPiece(
        PlannedRoom room,
        InteriorPieceDef marker,
        IReadOnlyList<CurrentBuilding> built,
        IReadOnlySet<Cell> taken
    )
    {
        foreach (var b in built)
        {
            if (b.Building!.Definition == PenMarkerDefinition && b.Cells.Count > 0 && room.Region.Contains(b.Cells[0]))
            {
                var r = PlacementSearchRules.CellsRectangle(b.Cells);
                return (
                    new WantedPiece
                    {
                        DefName = PenMarkerDefinition,
                        Minimum = new Cell(r.X, r.Z),
                        Maximum = new Cell(r.X + r.Width - 1, r.Z + r.Height - 1),
                    },
                    true
                );
            }
        }
        var size = marker.Size;
        if (string.IsNullOrEmpty(marker.Def))
        {
            return (new WantedPiece(), false);
        }
        if (size.X < 1 || size.Z < 1)
        {
            size = new Cell(1, 1);
        }
        var inner = room.Region.Bounds();
        var cx = inner.X + inner.Width / 2;
        var cz = inner.Z + inner.Height / 2;
        var best = default(Cell);
        var bestDist = 0;
        var found = false;
        foreach (var c in room.Region.Cells())
        {
            var fits = true;
            foreach (var m in PlannedRules.RectCells(new Rectangle { X = c.X, Z = c.Z, Width = size.X, Height = size.Z }))
            {
                fits = fits && room.Region.Contains(m) && !taken.Contains(m);
            }
            var d = (c.X - cx) * (c.X - cx) + (c.Z - cz) * (c.Z - cz);
            if (fits && (!found || d < bestDist))
            {
                best = c;
                bestDist = d;
                found = true;
            }
        }
        if (!found)
        {
            return (new WantedPiece(), false);
        }
        return (
            new WantedPiece
            {
                DefName = PenMarkerDefinition,
                Minimum = best,
                Maximum = new Cell(best.X + size.X - 1, best.Z + size.Z - 1),
                Size = size,
                Rot = Rotation.North,
            },
            true
        );
    }
}
