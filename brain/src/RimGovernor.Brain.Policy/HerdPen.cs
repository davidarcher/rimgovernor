namespace RimGovernor.Brain.Policy;

// The pen. A unit with roaming animals (RequiresPen: the races the game walks off the map unless they are fully
// enclosed) gets a pen reservation beside its barn, sharing one wall with it. The animal flap in that shared wall
// joins the two, so the barn and the pen are one enclosure; the pen's own fence has a gate for colonists. The pen
// is sited on ground that grows grazing and sized by the game's pen food sum (PenCells over the herd's PenDemand),
// and one PenMarker inside it claims the enclosure for the game's pen logic. Pen membership comes from the native
// enclosed-pen lookup, so a marker standing is not an animal contained.

/// <summary>
/// The pen a unit asks for: Race is the unit's key (the herd's race, empty for the misc unit;
/// <see cref="LayoutReservation.Herd"/>) and Cells the interior its animals' grazing needs.
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
        return PolicyFunctions.PlanHerd(f.HerdPlanInput()).PenWants(animals!, f.AnimalUpkeep.AnimalRaces, pasture!);
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
            if (a.RequiresPen.TryGetValue(out var pen) && pen)
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
            var (demand, known) = PolicyFunctions.PenDemand(races, units[unit]);
            if (known)
            {
                @out.Add(new PenWant(unit, PolicyFunctions.PenCells(demand, perCell)));
            }
        }
        return @out;
    }
}

public sealed partial record LayoutPlan
{
    /// <summary>The interior cells of the pens of the unit keyed herd.</summary>
    internal int PenCells(string herd)
    {
        var n = 0;
        foreach (var r in Reservations)
        {
            if (r.Kind == PolicyFunctions.ReservePen && r.Herd == herd)
            {
                var inner = PolicyFunctions.Pad(r.Area.Bounds(), -1);
                n += inner.Width * inner.Height;
            }
        }
        return n;
    }

    /// <summary>
    /// The cell of the wall pen shares with its unit's barn, where the animal flap joins them: the barn's wall
    /// cell, never a corner. Not ok when the pen shares no wall with a barn of its unit.
    /// </summary>
    internal (Cell Cell, bool Ok) PenLink(LayoutReservation pen)
    {
        foreach (var r in Reservations)
        {
            if (r.Kind != PolicyFunctions.ReserveBarn || r.Herd != pen.Herd)
            {
                continue;
            }
            var (c, ok) = PolicyFunctions.SharedWallLink(pen.Area.Bounds(), r.Area.Bounds());
            if (ok)
            {
                return (c, true);
            }
        }
        return (default, false);
    }

    /// <summary>The animal flap cells, one per pen, in plan order.</summary>
    internal IReadOnlyList<Cell> Flaps()
    {
        var @out = new List<Cell>();
        foreach (var r in Reservations)
        {
            if (r.Kind != PolicyFunctions.ReservePen)
            {
                continue;
            }
            var (c, ok) = PenLink(r);
            if (ok)
            {
                @out.Add(c);
            }
        }
        return @out;
    }

    /// <summary>
    /// The cells of r's ring that take an animal flap: the link of a pen in the wall of its barn, none for any
    /// other room (a pen's own ring holds the barn's wall there).
    /// </summary>
    public IReadOnlyList<Cell> FlapCells(PlannedRoom r) => RingCells(r, Flaps());

    private static IReadOnlyList<Cell> RingCells(PlannedRoom r, IReadOnlyList<Cell> cells)
    {
        if (r.Outdoor)
        {
            return [];
        }
        var ring = r.WallRing();
        return cells.Where(c => ring.Contains(c)).ToList();
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The def of an outdoor room's ring fence (the pen, the materials yard).</summary>
    public const string PenFenceDefinition = "Fence";

    /// <summary>The def of an outdoor room's ring gate.</summary>
    public const string PenGateDefinition = "FenceGate";

    /// <summary>The pen's one piece.</summary>
    public const string PenMarkerDefinition = "PenMarker";

    /// <summary>The marker's template slot.</summary>
    internal const string PenMarkerSlot = "pen.marker";

    /// <summary>
    /// The survey cells a pen may stand on: walkable open ground whose terrain grows grazing, with no rock, tree,
    /// ore, ruin, prop or edifice on it and no hazard.
    /// </summary>
    public static HashSet<Cell> GrazingGround(MapSurvey s, PastureTable pasture)
    {
        var @out = new HashSet<Cell>();
        foreach (var c in s.Cells)
        {
            var (_, grows) = pasture.PerCell(c.Terrain);
            if (grows && c.Walkable && !c.Rock && !c.Hazard && !c.Tree && !c.Ore && !c.Ruin && !c.Prop && !c.Built)
            {
                @out.Add(c.Cell);
            }
        }
        return @out;
    }

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

    /// <summary>
    /// Tops plan up with the pens wants ask for: each unit's shortfall in cells becomes a pen sharing one wall
    /// with the unit's first barn that has ground for it, wholly on grazing ground. Nothing placed moves; a pen
    /// that fits nowhere is left out, and a unit without a barn gets none. plan holds its core zones.
    /// </summary>
    public static LayoutPlan PlanPenSites(LayoutPlan plan, IReadOnlySet<Cell> grazing, params IReadOnlyList<PenWant> wants)
    {
        if (wants.Count == 0 || grazing.Count == 0 || plan.Rooms.Count == 0)
        {
            return plan;
        }
        var u = NewUtilityGrid(plan);
        foreach (var want in wants)
        {
            var @short = want.Cells - plan.PenCells(want.Race);
            if (@short <= 0)
            {
                continue;
            }
            var (iw, ih) = PenSide(@short);
            var (w, h) = WalledSide(iw, ih);
            var barns = plan.Reservations.Where(r => r.Kind == ReserveBarn && r.Herd == want.Race).Select(r => r.Area.Bounds()).ToList();
            foreach (var barn in barns)
            {
                var (site, ok) = u.BesideReservation(plan, barn, ReservePen, w, h, r => RectCells(Pad(r, -1)).All(grazing.Contains));
                if (ok)
                {
                    plan = u.Reserve(plan, new LayoutReservation { Kind = ReservePen, Area = RectRegion(site), Herd = want.Race });
                    break;
                }
            }
        }
        return plan;
    }

    /// <summary>Reports a PenMarker standing in room.</summary>
    public static bool PenMarkerStanding(PlannedRoom room, IReadOnlyList<CurrentBuilding> built)
    {
        foreach (var b in built)
        {
            if (b.Building.Definition() == PenMarkerDefinition && b.Cells.Count > 0 && room.Region.Contains(b.Cells[0]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reports the plan holding at least one pen and every pen enclosed with its marker standing: the fence and
    /// gate as planned, the barn's wall and flap beside it, one PenMarker inside.
    /// </summary>
    public static bool PensStand(LayoutPlan plan, GroundCensus ground, IReadOnlyList<CurrentBuilding> built)
    {
        var pens = plan.HerdRooms(PlannedPen);
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
            if (b.Building.Definition() == PenMarkerDefinition && b.Cells.Count > 0 && room.Region.Contains(b.Cells[0]))
            {
                var r = CellsRectangle(b.Cells);
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
            foreach (var m in RectCells(new Rectangle { X = c.X, Z = c.Z, Width = size.X, Height = size.Z }))
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
                Slot = PenMarkerSlot,
                Size = size,
                Rot = Rotation.North,
            },
            true
        );
    }
}
