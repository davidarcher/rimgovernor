namespace RimGovernor.Brain.Policy;

/// <summary>A plan drawn as one native overlay layer: output only, replaced whole on every draw.</summary>
public sealed class LayoutOverlay
{
    public List<OverlayLayer> Layers { get; init; } = [];
    public List<OverlayLabel> Labels { get; init; } = [];
}

/// <summary>One shape: a color, fill or outline, and its cells as rectangles and row runs. Label names it for tests and logs.</summary>
public sealed class OverlayLayer
{
    public OverlayColor Color { get; init; }
    public OverlayStyle Style { get; init; }
    public string Label { get; init; } = "";
    public List<Rectangle> Rects { get; init; } = [];
    public List<RowRun> Runs { get; init; } = [];

    /// <summary>Segments between cells.</summary>
    public List<OverlayLine> Lines { get; init; } = [];
}

/// <summary>Straight RGBA in [0,1].</summary>
public readonly record struct OverlayColor(float R, float G, float B, float A);

/// <summary>How a shape's cells draw.</summary>
public enum OverlayStyle
{
    /// <summary>Fills every cell.</summary>
    Fill = 1,

    /// <summary>Draws only the boundary edges of the shape's cells.</summary>
    Outline,
}

/// <summary>Text drawn over the map at a cell.</summary>
public readonly record struct OverlayLabel(string Text, Cell Cell);

/// <summary>A straight segment between two map cells, drawn centre to centre; Arrow puts a head on To.</summary>
public readonly record struct OverlayLine(Cell From, Cell To, bool Arrow = false);

/// <summary>A palette entry's RGB; fill and outline pick the alpha.</summary>
public readonly record struct OverlayHue(float R, float G, float B)
{
    public OverlayColor Fill() => new(R, G, B, 0.3f);

    public OverlayColor Outline() => new(R, G, B, 0.85f);
}

/// <summary>A role's hue and label.</summary>
public readonly record struct OverlayStyleOf(OverlayHue Hue, string Label);

public static partial class PolicyFunctions
{
    public static OverlayHue PlanGray => new(0.6f, 0.6f, 0.6f);
    public static OverlayHue PlanRed => new(0.9f, 0.2f, 0.2f);
    public static OverlayHue PlanYellow => new(0.95f, 0.85f, 0.2f);
    public static OverlayHue PlanGreen => new(0.3f, 0.8f, 0.3f);
    public static OverlayHue PlanCyan => new(0.2f, 0.85f, 0.9f);
    public static OverlayHue PlanBlue => new(0.25f, 0.4f, 0.95f);
    public static OverlayHue PlanPink => new(0.95f, 0.45f, 0.75f);
    public static OverlayHue PlanLightBlue => new(0.55f, 0.75f, 1f);
    public static OverlayHue PlanAmber => new(1f, 0.65f, 0.1f);
    public static OverlayHue PlanWhite => new(0.95f, 0.95f, 0.95f);
    public static OverlayHue PlanDarkPurple => new(0.4f, 0.15f, 0.5f);
    public static OverlayHue PlanBrown => new(0.55f, 0.35f, 0.2f);
    public static OverlayHue PlanTan => new(0.8f, 0.7f, 0.5f);
    public static OverlayHue PlanViolet => new(0.6f, 0.4f, 0.9f);

    private static OverlayStyleOf St(OverlayHue hue, string label) => new(hue, label);

    /// <summary>The overlay style tables: nested so their initializers run on first use, after every PolicyFunctions property they read exists.</summary>
    private static class OverlayTables
    {
        public static readonly Dictionary<ZoneKind, OverlayStyleOf> Zone = new()
        {
            [ZoneField] = St(PlanGreen, "field"),
            [ZoneMining] = St(PlanBrown, "mining"),
            [ZoneNoGo] = St(PlanDarkPurple, "no-go"),
        };

        public static readonly Dictionary<PlannedRole, OverlayStyleOf> Room = new()
        {
            [PlannedBedroom] = St(PlanBlue, "bedroom"),
            [PlannedSuite] = St(PlanBlue, "suite"),
            [PlannedShelter] = St(PlanLightBlue, "shelter"),
            [PlannedRole.Airlock] = St(PlanLightBlue, "airlock"),
            [PlannedPorch] = St(PlanLightBlue, "porch"),
            [PlannedDining] = St(PlanAmber, "dining"),
            [PlannedRec] = St(PlanPink, "rec room"),
            [PlannedLab] = St(PlanViolet, "research"),
            [PlannedBattery] = St(PlanCyan, "battery room"),
            [PlannedHospital] = St(PlanWhite, "hospital"),
            [PlannedPrison] = St(PlanDarkPurple, "jail"),
            [PlannedKitchen] = St(PlanYellow, "kitchen"),
            [PlannedFreezer] = St(PlanCyan, "freezer"),
            [PlannedRole.MealCloset] = St(PlanCyan, "meal closet"),
            [PlannedButchery] = St(PlanBrown, "butchery"),
            [PlannedStorage] = St(PlanTan, "storage"),
            [PlannedRole.Armory] = St(PlanTan, "armory"),
            [PlannedRole.Wardrobe] = St(PlanTan, "wardrobe"),
            [PlannedWorkshop] = St(PlanBrown, "workshop"),
            [PlannedReserve] = St(PlanGray, "reserve"),
            [PlannedRole.Circulation] = St(PlanGray, "circulation"),
            [PlannedTomb] = St(PlanGray, "tomb"),
            [PlannedMorgue] = St(PlanCyan, "morgue"),
            [PlannedWasteYard] = St(PlanRed, "waste yard"),
            [PlannedRole.Incinerator] = St(PlanRed, "incinerator"),
            [PlannedGraveyard] = St(PlanGreen, "graveyard"),
            [PlannedTrainingRange] = St(PlanBrown, "training range"),
            [PlannedSparringRing] = St(PlanBrown, "sparring ring"),
            [PlannedYard] = St(PlanTan, "materials yard"),
            [PlannedThrone] = St(PlanAmber, "throne room"),
            [PlannedRole.Nursery] = St(PlanGreen, "nursery"),
            [PlannedRole.Playroom] = St(PlanYellow, "playroom"),
            [PlannedRole.Classroom] = St(PlanWhite, "classroom"),
            [PlannedRole.DeathrestChamber] = St(PlanDarkPurple, "deathrest chamber"),
            [PlannedRole.Worship] = St(PlanAmber, "worship room"),
            [PlannedRole.ContainmentCell] = St(PlanDarkPurple, "containment cell"),
            [PlannedRole.IsolationRoom] = St(PlanDarkPurple, "isolation room"),
        };

        public static readonly Dictionary<ReservationKind, OverlayStyleOf> Reservation = new()
        {
            [ReserveBatteryRoom] = St(PlanCyan, "battery room"),
            [ReserveGeothermal] = St(PlanCyan, "geothermal"),
            [ReserveYard] = St(PlanTan, "materials yard"),
            [ReservationKind.Barn] = St(PlanBrown, "barn"),
            [ReservationKind.Pen] = St(PlanGreen, "pen"),
        };

        public static readonly Dictionary<NodeKind, OverlayStyleOf> Site = new()
        {
            [NodeRing] = St(PlanRed, "perimeter"),
            [NodeGate] = St(PlanYellow, "gate"),
            [NodePump] = St(PlanCyan, "moisture pump"),
            [NodeKillbox] = St(PlanRed, "killbox"),
            [NodeFence] = St(PlanYellow, "killbox fence"),
            [NodeMortar] = St(PlanRed, "mortar"),
            [NodeCoverClear] = St(PlanGray, "clear cover"),
            [NodePocketWall] = St(PlanRed, "pocket wall"),
            [NodeVent] = St(PlanCyan, "vent"),
            [NodeTurbine] = St(PlanCyan, "turbine"),
            [NodeSolar] = St(PlanCyan, "solar"),

            // The defense tiers PlanDefenseTiers cuts under the killbox.
            [NodeKind.DefCorridor] = St(PlanRed, "corridor"),
            [NodeKind.DefLane] = St(PlanAmber, "trap lane"),
            [NodeKind.DefFunnel] = St(PlanRed, "funnel"),
            [NodeKind.DefFence] = St(PlanYellow, "kill fence"),
            [NodeKind.DefTraps] = St(PlanPink, "spike traps"),
            [NodeKind.DefFiring] = St(PlanGreen, "firing line"),
            [NodeKind.DefShooter] = St(PlanBlue, "shooter floor"),
            [NodeKind.DefTurrets] = St(PlanCyan, "turrets"),
            [NodeKind.DefMortars] = St(PlanViolet, "mortars"),
            [NodeKind.DefIEDs] = St(PlanBrown, "IEDs"),
            [NodeKind.DefBait] = St(PlanTan, "bait"),
        };
    }

    // Ring attributes drawn beside the ring itself.
    internal static OverlayStyleOf BridgeOverlay => St(PlanBrown, "bridge");
    internal static OverlayStyleOf GapOverlay => St(PlanYellow, "open gap");
    internal static OverlayStyleOf LaneOverlay => St(PlanGreen, "turbine lane");

    /// <summary>The rectangle r inside bounds, null when nothing is left.</summary>
    internal static Rectangle? Clip(Rectangle r, Bounds b)
    {
        var minX = Math.Max(r.X, 0);
        var minZ = Math.Max(r.Z, 0);
        var maxX = Math.Min(r.X + r.Width, b.Width);
        var maxZ = Math.Min(r.Z + r.Height, b.Height);
        if (minX >= maxX || minZ >= maxZ)
        {
            return null;
        }
        return new Rectangle { X = minX, Z = minZ, Width = maxX - minX, Height = maxZ - minZ };
    }

    internal static RowRun? ClipRun(RowRun r, Bounds b)
    {
        var c = Clip(new Rectangle { X = r.X, Z = r.Z, Width = r.Length, Height = 1 }, b);
        return c is { } k ? new RowRun(k.Z, k.X, k.Width) : null;
    }

    /// <summary>Packs cells into row runs of consecutive x, z-major.</summary>
    internal static List<RowRun> CellRuns(IReadOnlyCollection<Cell> cells)
    {
        var rows = new Dictionary<int, HashSet<int>>();
        int minZ = 1 << 30, maxZ = -(1 << 30), minX = 1 << 30, maxX = -(1 << 30);
        foreach (var c in cells)
        {
            if (!rows.TryGetValue(c.Z, out var row))
            {
                rows[c.Z] = row = [];
            }
            row.Add(c.X);
            (minZ, maxZ, minX, maxX) = (Math.Min(minZ, c.Z), Math.Max(maxZ, c.Z), Math.Min(minX, c.X), Math.Max(maxX, c.X));
        }
        var result = new List<RowRun>();
        for (var z = minZ; z <= maxZ; z++)
        {
            if (!rows.TryGetValue(z, out var row))
            {
                continue;
            }
            for (var x = minX; x <= maxX; x++)
            {
                if (!row.Contains(x))
                {
                    continue;
                }
                var start = x;
                while (row.Contains(x + 1))
                {
                    x++;
                }
                result.Add(new RowRun(z, start, x - start + 1));
            }
        }
        return result;
    }

    /// <summary>One layer of the lines with both ends in bounds; null when none remain.</summary>
    private static OverlayLayer? LineLayer(OverlayStyleOf style, Bounds bounds, IEnumerable<OverlayLine> lines)
    {
        bool Inside(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < bounds.Width && c.Z < bounds.Height;
        var kept = lines.Where(l => Inside(l.From) && Inside(l.To)).ToList();
        return kept.Count == 0 ? null : new OverlayLayer { Style = OverlayStyle.Outline, Label = style.Label, Color = style.Hue.Outline(), Lines = kept };
    }

    /// <summary>The region cell nearest the middle of its bounds, so a ring's label lands on its floor rather than in its hole.</summary>
    internal static Cell LabelCell(Region g)
    {
        var b = g.Bounds();
        var mid = new Cell(b.X + (b.Width / 2), b.Z + (b.Height / 2));
        var best = mid;
        var found = false;
        foreach (var c in g.Cells())
        {
            if (!found || DistanceSquared(c, mid) < DistanceSquared(best, mid))
            {
                (best, found) = (c, true);
            }
        }
        return best;
    }

    /// <summary>Moves o's field zone layers into their own overlay, so the native can hide the busy field-block lattice on its own.</summary>
    public static (LayoutOverlay Others, LayoutOverlay Fields) SplitFields(LayoutOverlay o)
    {
        var rest = new LayoutOverlay { Labels = o.Labels };
        var fields = new LayoutOverlay();
        foreach (var l in o.Layers)
        {
            if (l.Label == OverlayTables.Zone[ZoneField].Label)
            {
                fields.Layers.Add(l);
            }
            else
            {
                rest.Layers.Add(l);
            }
        }
        return (rest, fields);
    }

    internal static Region UnionRegions(Region a, Region b) => RegionOfRuns([.. a.Runs, .. b.Runs]);

    internal static OverlayStyleOf? RoomOverlayStyle(PlannedRole role) => OverlayTables.Room.TryGetValue(role, out var s) ? s : null;

    internal static OverlayStyleOf? ZoneOverlayStyle(ZoneKind kind) => OverlayTables.Zone.TryGetValue(kind, out var s) ? s : null;

    internal static OverlayStyleOf? ReservationOverlayStyle(ReservationKind kind) => OverlayTables.Reservation.TryGetValue(kind, out var s) ? s : null;

    internal static OverlayStyleOf? SiteOverlayStyle(NodeKind kind) => OverlayTables.Site.TryGetValue(kind, out var s) ? s : null;

    internal static OverlayLayer? OverlayLineLayer(OverlayStyleOf style, Bounds bounds, IEnumerable<OverlayLine> lines) => LineLayer(style, bounds, lines);
}

public sealed partial record LayoutPlan
{
    /// <summary>Each room's label cell by room id.</summary>
    private Dictionary<int, Cell> RoomCentres()
    {
        var centres = new Dictionary<int, Cell>();
        foreach (var r in Rooms)
        {
            centres[r.Id] = LabelCell(r.Region);
        }
        return centres;
    }

    /// <summary>
    /// Adds the plan's line layers to out: the door graph (each link as two segments through its door cell), a
    /// successor arrow from every room to the room that replaces it, and the room trips as affinity edges. A line at
    /// an absent room or outside bounds is dropped.
    /// </summary>
    private void LinesOverlay(Bounds bounds, LayoutOverlay output)
    {
        var centres = RoomCentres();
        var links = new List<OverlayLine>();
        var successors = new List<OverlayLine>();
        var trips = new List<OverlayLine>();
        foreach (var l in Links)
        {
            if (centres.TryGetValue(l.A, out var a) && centres.TryGetValue(l.B, out var b))
            {
                links.Add(new OverlayLine(a, l.Cell));
                links.Add(new OverlayLine(l.Cell, b));
            }
        }
        foreach (var r in Rooms)
        {
            if (r.Replaces > 0 && centres.TryGetValue(r.Replaces, out var old))
            {
                successors.Add(new OverlayLine(old, centres.GetValueOrDefault(r.Id), true));
            }
        }
        foreach (var t in Trips)
        {
            if (centres.TryGetValue(t.A, out var a) && centres.TryGetValue(t.B, out var b))
            {
                trips.Add(new OverlayLine(a, b));
            }
        }
        var sets = new (OverlayStyleOf Style, List<OverlayLine> Lines)[]
        {
            (new OverlayStyleOf(PlanWhite, "link"), links),
            (new OverlayStyleOf(PlanAmber, "successor arrow"), successors),
            (new OverlayStyleOf(PlanCyan, "trip"), trips),
        };
        foreach (var (style, lines) in sets)
        {
            if (OverlayLineLayer(style, bounds, lines) is { } layer)
            {
                output.Layers.Add(layer);
            }
        }
    }

    /// <summary>Adds the station advice's required edges (a link the environment needs, cold storage beside the stove) to o as red lines between the first matched built room of each group.</summary>
    public void RequirementOverlay(StationAdvice advice, Bounds bounds, LayoutOverlay o)
    {
        var centres = RoomCentres();
        var groupOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < advice.Groups.Count; i++)
        {
            foreach (var s in advice.Groups[i].Group.Stations)
            {
                groupOf[s.Node] = i;
            }
        }
        (int Index, Cell Cell)? Room(string node)
        {
            if (!groupOf.TryGetValue(node, out var i) || advice.Groups[i].Rooms.Count == 0 || !centres.TryGetValue(advice.Groups[i].Rooms[0], out var c))
            {
                return null;
            }
            return (i, c);
        }
        var lines = new List<OverlayLine>();
        foreach (var e in advice.Graph.Edges)
        {
            if (!e.Required)
            {
                continue;
            }
            if (Room(e.A) is { } a && Room(e.B) is { } b && a.Index != b.Index)
            {
                lines.Add(new OverlayLine(a.Cell, b.Cell));
            }
        }
        if (OverlayLineLayer(new OverlayStyleOf(PlanRed, "requirement"), bounds, lines) is { } layer)
        {
            o.Layers.Add(layer);
        }
    }

    /// <summary>Draws the plan inside bounds. Layer order is zones, reservations, rooms, so the smaller shapes draw last.</summary>
    public LayoutOverlay Overlay(Bounds bounds)
    {
        var output = new LayoutOverlay();
        void Label(string text, Cell at)
        {
            if (at.X >= 0 && at.Z >= 0 && at.X < bounds.Width && at.Z < bounds.Height)
            {
                output.Labels.Add(new OverlayLabel(text, at));
            }
        }
        bool Add(OverlayStyleOf style, OverlayStyle shape, IEnumerable<Rectangle>? rects, IEnumerable<RowRun>? runs)
        {
            var layer = new OverlayLayer { Style = shape, Label = style.Label, Color = shape == OverlayStyle.Outline ? style.Hue.Outline() : style.Hue.Fill() };
            foreach (var r in rects ?? [])
            {
                if (Clip(r, bounds) is { } c)
                {
                    layer.Rects.Add(c);
                }
            }
            foreach (var r in runs ?? [])
            {
                if (ClipRun(r, bounds) is { } c)
                {
                    layer.Runs.Add(c);
                }
            }
            if (layer.Rects.Count + layer.Runs.Count == 0)
            {
                return false;
            }
            output.Layers.Add(layer);
            return true;
        }
        static Cell Centre(Rectangle r) => new(r.X + (r.Width / 2), r.Z + (r.Height / 2));

        foreach (var z in Zones)
        {
            if (ZoneOverlayStyle(z.Kind) is { } style)
            {
                Add(style, OverlayStyle.Fill, null, z.Runs);
            }
        }
        foreach (var r in Reservations)
        {
            if (ReservationOverlayStyle(r.Kind) is not { } style)
            {
                continue;
            }
            if (Add(style, OverlayStyle.Fill, [r.Area.Bounds()], null))
            {
                Label(style.Label, Centre(r.Area.Bounds()));
            }
        }

        // The defense nodes draw as their own cells. The cover band is one wide region: one label, on its first run.
        foreach (var s in Sites)
        {
            if (SiteOverlayStyle(s.Kind) is not { } style)
            {
                continue;
            }
            var shape = s.Kind == NodeRing || s.Kind == NodeKillbox ? OverlayStyle.Outline : OverlayStyle.Fill;

            // A rectangle goes out as one, any other shape as its row runs.
            IReadOnlyList<Rectangle>? rects = null;
            IReadOnlyList<RowRun>? runs = s.Footprint.Runs;
            if (s.Footprint.IsRect())
            {
                rects = [s.Footprint.Bounds()];
                runs = null;
            }
            foreach (var l in s.Lanes)
            {
                Add(LaneOverlay, OverlayStyle.Fill, null, l.Region.Runs);
            }
            var drawn = Add(style, shape, rects, runs);
            if (s.Ring is not null)
            {
                Add(BridgeOverlay, OverlayStyle.Fill, null, s.Ring.Bridged.Runs);
                Add(GapOverlay, OverlayStyle.Fill, null, s.Ring.Gaps.Runs);
            }
            else if (drawn && s.Kind == NodeCoverClear)
            {
                var first = s.Footprint.Runs[0];
                Label(style.Label, new Cell(first.X + (first.Length / 2), first.Z));
            }
            else if (drawn)
            {
                Label(style.Label, Centre(s.Footprint.Bounds()));
            }
        }
        foreach (var r in Rooms)
        {
            if (RoomOverlayStyle(r.Role) is not { } style)
            {
                continue;
            }

            // The room's own cells, not its bounding box: the outline follows the floor and its wall ring, so an L or a ring draws as itself.
            var shape = r.Region;
            if (!r.Unfenced())
            {
                shape = UnionRegions(r.Region, r.Region.Walls());
            }
            var text = style.Label;
            if (r.Id > 0)
            {
                text = $"#{r.Id} {style.Label}";
            }
            if (r.Replaces > 0)
            {
                // A successor also fills its floor, so it reads apart from the room it replaces.
                text += $" (replaces #{r.Replaces})";
                Add(new OverlayStyleOf(style.Hue, "successor"), OverlayStyle.Fill, null, r.Region.Runs);
            }
            if (Add(style, OverlayStyle.Outline, null, shape.Runs))
            {
                Label(text, LabelCell(r.Region));
            }
        }
        var doors = new List<Cell>();
        foreach (var r in Rooms)
        {
            if (r.Unfenced())
            {
                continue;
            }
            doors.AddRange(DoorsOf(r));
        }
        Add(new OverlayStyleOf(PlanWhite, "door"), OverlayStyle.Fill, null, CellRuns(doors));
        LinesOverlay(bounds, output);
        return output;
    }
}
