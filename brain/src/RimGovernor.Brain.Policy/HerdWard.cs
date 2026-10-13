namespace RimGovernor.Brain.Policy;

// The hospital's animal ward. A sick animal is treated in the hospital, not in a room of the herd's: a wound's
// infection chance reads the room the patient lies in (cleanliness), a barn with sixteen cows is filthy, and
// surgery applies the bed's success factor only to a patient lying in a bed. The game gives an animal no human bed
// (RestUtility.CanUseBedEver wants the bed's bed_humanlike to match the pawn's), so the hospital also holds
// AnimalBeds: beds that are not humanlike, so they neither change the hospital's room role nor count as a patient
// bed (SelectHospitalBed counts humanlike beds only).
//
// The ward is the hospital's free floor beside its human beds that holds an animal bed and keeps the room
// walkable; it is sized from the animals that need it (WardBeds) and the slots are whatever the room has left.
// Each standing ward bed is flagged medical (a BedUse patch; any bed can carry the flag natively), so colonists
// treat animals in it.

/// <summary>
/// What the layout exposes of the hospital's animal ward to the herd plan. Ready is a standing hospital with a
/// built medical animal bed; Area is the allowed-area id covering the hospital, the area an animal is let into for
/// surgery. Either unknown (or Ready false) means no sterilize.
/// </summary>
public sealed record AnimalWard
{
    public Fact<bool> Ready { get; init; }
    public Fact<string> Area { get; init; }
}

public sealed partial record LayoutPlan
{
    /// <summary>The interior cells of the plan's hospitals, sorted.</summary>
    public IReadOnlyList<Cell> HospitalCells()
    {
        var set = new HashSet<Cell>();
        foreach (var room in Hospitals())
        {
            foreach (var c in PolicyFunctions.RectCells(room.Region.Bounds()))
            {
                set.Add(c);
            }
        }
        return PolicyFunctions.SortedCells(set);
    }

    /// <summary>The plan's hospital rooms, in plan order.</summary>
    internal IReadOnlyList<PlannedRoom> Hospitals() => Rooms.Where(r => r.Role == PolicyFunctions.PlannedHospital).ToList();
}

public static partial class PolicyFunctions
{
    /// <summary>The bot area key of the Hospital allowed area: the interior of the plan's hospitals. It is the area SterilizeChoice lets an animal into for surgery.</summary>
    public const string HospitalAreaKey = "Hospital";

    /// <summary>The native label of the Hospital area.</summary>
    public const string HospitalAreaLabel = HospitalAreaKey;

    /// <summary>
    /// The animal beds the hospital is asked for: one per animal that needs tending now (a condition not yet
    /// tended, or a life threat), and one while a sterilize surgery is wanted, since the surgery needs a bed to
    /// lie in and an animal that is not hurt would otherwise never get one. Unknown care facts count nothing: the
    /// ward is topped up when they are read.
    /// </summary>
    public static int WardBeds(IReadOnlyList<UpkeepAnimal> rows, HerdPolicy herd)
    {
        var n = 0;
        foreach (var a in rows)
        {
            if (AnimalNeedsTending(a))
            {
                n++;
            }
        }
        if (n == 0 && SterilizeWanted(rows, herd).Count > 0)
        {
            n = 1;
        }
        return n;
    }

    /// <summary>An animal with a condition no one has tended or that threatens its life.</summary>
    internal static bool AnimalNeedsTending(UpkeepAnimal a)
    {
        if (a.LifeThreatening.TryGetValue(out var life) && life)
        {
            return true;
        }
        if (!a.Conditions.TryGetValue(out var conditions))
        {
            return false;
        }
        foreach (var c in conditions)
        {
            if (c.Tended.TryGetValue(out var tended) && !tended)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The animal bed slots of a hospital room, nearest the far wall first: floor outside the human template (the
    /// hospital's own beds and monitors) where an animal bed fits, the doorways stay reachable, and every bed
    /// keeps a free cell beside it to be tended from. Empty for a room the hospital template does not plan, or a
    /// bed whose shape is unread.
    /// </summary>
    public static IReadOnlyList<InteriorPiece> WardSlots(PlannedRoom room, PieceShapes shapes, InteriorPieceDef bed)
    {
        if (room.Role != PlannedHospital || string.IsNullOrEmpty(bed.Def) || bed.Size.X < 1 || bed.Size.Z < 1)
        {
            return [];
        }
        var (inner, ok) = InteriorRoomFromLayout(room, shapes);
        if (!ok)
        {
            return [];
        }
        var (human, planned) = PlanRoomInterior(inner, new InteriorPieceDef());
        if (!planned)
        {
            return [];
        }
        var blocked = new HashSet<Cell>();
        foreach (var p in human.Pieces)
        {
            foreach (var c in RectCells(p.Rect))
            {
                blocked.Add(c);
            }
        }
        // Candidates: every footprint in the interior, furthest from the doors first, so the ward fills from the back and the entrance stays open.
        var thresholds = InteriorThresholds(inner);
        int Dist(Rectangle r)
        {
            var best = 1 << 30;
            foreach (var t in thresholds)
            {
                var d = Math.Abs(r.X - t.X) + Math.Abs(r.Z - t.Z);
                best = Math.Min(best, d);
            }
            return best;
        }
        var cands = new List<Rectangle>();
        for (var z = inner.Interior.Z; z + bed.Size.Z <= inner.Interior.Z + inner.Interior.Height; z++)
        {
            for (var x = inner.Interior.X; x + bed.Size.X <= inner.Interior.X + inner.Interior.Width; x++)
            {
                cands.Add(new Rectangle { X = x, Z = z, Width = bed.Size.X, Height = bed.Size.Z });
            }
        }
        var ordered = cands.OrderByDescending(Dist).ThenBy(r => r.X).ThenBy(r => r.Z).ToList();
        var @out = new List<InteriorPiece>();
        foreach (var r in ordered)
        {
            var cells = RectCells(r);
            var free = true;
            foreach (var c in cells)
            {
                free = free && !blocked.Contains(c);
            }
            if (!free || !InteriorPlacementWalkable(inner, blocked, cells) || !WardBedsStayTended(inner.Interior, blocked, @out, cells))
            {
                continue;
            }
            foreach (var c in cells)
            {
                blocked.Add(c);
            }
            var piece = NewInteriorPiece(FormattableString.Invariant($"ward.{@out.Count + 1}"), bed.Def, bed.Size, Rotation.North, new Cell(r.X, r.Z));
            @out.Add(piece with { Rect = r });
        }
        return @out;
    }

    /// <summary>Reports that, with cells blocked too, every ward bed (those placed and the new one) still has a free floor cell beside it.</summary>
    private static bool WardBedsStayTended(Rectangle interior, IReadOnlySet<Cell> blocked, IReadOnlyList<InteriorPiece> placed, IReadOnlyList<Cell> cells)
    {
        var now = new HashSet<Cell>(blocked);
        foreach (var c in cells)
        {
            now.Add(c);
        }
        var beds = new List<IReadOnlyList<Cell>> { cells };
        foreach (var p in placed)
        {
            beds.Add(RectCells(p.Rect));
        }
        foreach (var bed in beds)
        {
            var free = false;
            foreach (var c in bed)
            {
                foreach (var n in new[] { new Cell(c.X + 1, c.Z), new Cell(c.X - 1, c.Z), new Cell(c.X, c.Z + 1), new Cell(c.X, c.Z - 1) })
                {
                    free = free || (RectContains(interior, n) && !now.Contains(n));
                }
            }
            if (!free)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The first standing bed of def in room whose census row reads not medical; a bed the census lacks or whose flag is unread is not due yet.</summary>
    internal static (string Bed, bool Due) UnflaggedWardBed(PlannedRoom room, IReadOnlyList<CurrentBuilding> built, IReadOnlyList<SleepingBed> sleeping, string def)
    {
        foreach (var b in built)
        {
            if (b.Building.Definition() != def || b.Cells.Count == 0 || !RectInside(room.Region.Bounds(), CellsRectangle(b.Cells)))
            {
                continue;
            }
            foreach (var s in sleeping)
            {
                if (s.ID == b.ID && s.Medical.TryGetValue(out var medical) && !medical)
                {
                    return (b.ID, true);
                }
            }
        }
        return ("", false);
    }

    /// <summary>
    /// AnimalWard.Ready: known true when a hospital of the plan stands shelled (CensusRoomIn) with at least one
    /// standing animal bed whose census row reads medical. Known false when the plan has no hospital, none stands,
    /// or none of its standing beds is flagged. Unknown when a standing bed has no census row or an unread medical
    /// flag and no other bed proves the ward ready.
    /// </summary>
    public static Fact<bool> WardReady(LayoutPlan plan, RoomObservation rooms, IReadOnlyList<CurrentBuilding> built, IReadOnlyList<SleepingBed> beds, string animalBed)
    {
        var unread = false;
        foreach (var room in plan.Hospitals())
        {
            var (_, standing) = CensusRoomIn(room, rooms);
            if (!standing)
            {
                continue;
            }
            foreach (var b in built)
            {
                if (b.Building.Definition() != animalBed || b.Cells.Count == 0 || !RectInside(room.Region.Bounds(), CellsRectangle(b.Cells)))
                {
                    continue;
                }
                var read = false;
                foreach (var s in beds)
                {
                    if (s.ID != b.ID)
                    {
                        continue;
                    }
                    var known = s.Medical.TryGetValue(out var medical);
                    if (known && medical)
                    {
                        return Known(true);
                    }
                    read = read || known;
                }
                unread = unread || !read;
            }
        }
        if (unread)
        {
            return Unknown<bool>();
        }
        return Known(false);
    }

    /// <summary>
    /// The hospital's animal ward as a reconcile template: the animal beds standing in room (up to quota) where
    /// they stand, the missing ones in the first free ward slots. Absent reports a planned bed the room lacks.
    /// </summary>
    internal static (IReadOnlyList<WantedPiece> Template, bool Absent) WardTemplate(
        PlannedRoom room,
        PieceShapes shapes,
        InteriorPieceDef bed,
        int quota,
        IReadOnlyList<CurrentBuilding> built,
        IReadOnlySet<Cell> taken
    )
    {
        var standing = new List<WantedPiece>();
        foreach (var b in built)
        {
            if (b.Building.Definition() == bed.Def && b.Cells.Count > 0 && RectInside(room.Region.Bounds(), CellsRectangle(b.Cells)))
            {
                var r = CellsRectangle(b.Cells);
                standing.Add(
                    new WantedPiece
                    {
                        DefName = bed.Def,
                        Minimum = new Cell(r.X, r.Z),
                        Maximum = new Cell(r.X + r.Width - 1, r.Z + r.Height - 1),
                    }
                );
            }
        }
        var have = Math.Min(quota, standing.Count);
        var template = standing.Take(have).ToList();
        var absent = false;
        foreach (var p in WardSlots(room, shapes, bed))
        {
            if (have >= quota)
            {
                break;
            }
            if (RectCells(p.Rect).Any(taken.Contains))
            {
                continue;
            }
            template.Add(p.Wanted());
            absent = true;
            have++;
        }
        return (template, absent);
    }
}
