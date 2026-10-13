using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RimGovernor.Brain.Policy;

// The defense layout builds on the layout plan: the corridor, firing line and turrets stand in the plan's
// killbox, and the perimeter tier raises the plan's 3-thick wall with its gates in staged sections.
public static partial class PolicyConstants
{
    /// <summary>Names the perimeter's sections: this prefix plus a two-digit order, or TierGeothermal for the geyser's enclosure.</summary>
    public const string TierPerimeterPrefix = "perimeter-";

    public static readonly DefenseTierName TierGeothermal = new(TierPerimeterPrefix + "geothermal");

    /// <summary>A section's length along the wall: 20 cells of a 3-thick wall is about 60 blocks.</summary>
    internal const int PerimeterSectionRun = 20;

    // The perimeter's footings on soft ground: a plain bridge holds a wooden wall only; a heavy bridge, once
    // researched, holds stone.
    public const string PerimeterBridge = "Bridge";
    public const string PerimeterHeavyBridge = "HeavyBridge";
    public const string PerimeterHeavyResearch = "HeavyBridges";
    public const string PerimeterLightStuff = "WoodLog";

    /// <summary>Names the moisture pump sections under the perimeter's prefix: one per pump site, with its conduit run.</summary>
    public const string TierPumpPrefix = TierPerimeterPrefix + "pump-";

    /// <summary>Names the killbox opening's fence section under the perimeter's prefix.</summary>
    public const string TierFencePrefix = TierPerimeterPrefix + "fence-";
}

/// <summary>
/// One staged piece of the wall. Nodes are the plan's site nodes it is cut from (the ring and the gates in it,
/// a pump, the fence, the geothermal enclosure): the defense record stores them instead of the buildings, and
/// the buildings are resolved from the plan again by the section's name. A section cut from no node (lights,
/// pockets, prison turrets) names none.
/// </summary>
public sealed record PerimeterSection
{
    public DefenseTierName Name { get; init; }

    public List<string> Nodes { get; init; } = [];

    public List<Building> Buildings { get; init; } = [];
}

public static partial class PolicyFunctions
{
    private static readonly Regex PerimeterRevision = new(@"^r\d+-", RegexOptions.CultureInvariant);

    /// <summary>Reports whether a tier is a perimeter section.</summary>
    public static bool IsPerimeterTier(DefenseTierName name) =>
        name.Value.Length > TierPerimeterPrefix.Length && name.Value.StartsWith(TierPerimeterPrefix, StringComparison.Ordinal);

    /// <summary>The section's name before a re-cut put its revision in front: perimeter-r2-03 and perimeter-03 are both "03".</summary>
    public static string PerimeterSectionBase(DefenseTierName name)
    {
        var rest = name.Value.StartsWith(TierPerimeterPrefix, StringComparison.Ordinal) ? name.Value[TierPerimeterPrefix.Length..] : name.Value;
        var m = PerimeterRevision.Match(rest);
        return m.Success ? rest[m.Length..] : rest;
    }

    /// <summary>
    /// The live killbox node's id and a digest of its footprint, the identity a stored defense layout was
    /// proposed on. Ok is false while the plan has no opening.
    /// </summary>
    public static (string Id, string Digest, bool Ok) KillboxNode(LayoutPlan plan)
    {
        var (node, found) = plan.Killbox();
        if (!found)
        {
            return (string.Empty, string.Empty, false);
        }
        var text = new StringBuilder();
        foreach (var c in node!.Footprint.Cells())
        {
            text.Append(CultureInfo.InvariantCulture, $"{c.X},{c.Z};");
        }
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return (node.Node, Convert.ToHexStringLower(sum.AsSpan(0, 8)), true);
    }

    /// <summary>Reads the plan's killbox opening alone: the anchor cell, the direction toward home, the width and depth. Ok is false while the plan has no opening.</summary>
    public static (DefenseKillbox Killbox, bool Ok) KillboxOpening(LayoutPlan plan)
    {
        var (k, _, _, ok) = LayoutOpening(plan);
        return (k, ok);
    }

    /// <summary>
    /// The smallest valid layout plan whose killbox opens toward the given rotation: a 21x21 killbox beyond the
    /// entry cell (30, 30), the 3-wide approach leg ending beside the entry cell and one room to make the plan
    /// valid. A fixture or replay that stands for a world's plan only for the direction combat reads from it
    /// builds this.
    /// </summary>
    public static LayoutPlan OpeningPlan(Rotation toward)
    {
        // The killbox lies on the far side of the entry, past the wall's depth.
        var approach = new Rectangle(29, 29, 3, 1);
        var killbox = new Rectangle(20, 33, 21, 21);
        switch (toward)
        {
            case Rotation.South:
                approach = new Rectangle(29, 31, 3, 1);
                killbox = new Rectangle(20, 7, 21, 21);
                break;
            case Rotation.East:
                approach = new Rectangle(29, 29, 1, 3);
                killbox = new Rectangle(33, 20, 21, 21);
                break;
            case Rotation.West:
                approach = new Rectangle(31, 29, 1, 3);
                killbox = new Rectangle(7, 20, 21, 21);
                break;
            default:
                break;
        }
        PlannedSite Site(string id, NodeKind kind, Rectangle r) =>
            new() { Node = id, Footprint = RectRegion(r), Kind = kind };
        return new LayoutPlan
        {
            Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Storage, Region = RectRegion(new Rectangle(10, 10, 6, 4)), Door = new Cell(12, 14), DoorRot = Rotation.South }],
            NextRoomId = 2,
            Sites = [Site("killbox", NodeKind.Killbox, killbox), Site("killbox_approach-00", NodeKind.Approach, approach)],
        };
    }

    /// <summary>
    /// Reads the plan's killbox opening: the anchor, the census region around it (inside bounds) and the home
    /// cell behind the killbox's back wall. Ok is false while the plan has no opening.
    /// </summary>
    public static (DefenseKillbox Killbox, Rectangle Region, Cell Home, bool Ok) LayoutKillbox(LayoutPlan plan, Bounds bounds)
    {
        var (k, killbox, approaches, ok) = LayoutOpening(plan);
        if (!ok)
        {
            return (default!, default, default, false);
        }
        // Home is the yard cell behind the killbox's back-wall doorway.
        var home = AddCell(k.Entry, Scale(DirectionOf(k.Toward), PerimeterThick + k.Depth));
        var cover = killbox;
        foreach (var a in approaches.Append(RectOf(k.Entry, k.Entry)))
        {
            cover = UnionRect(cover, a);
        }
        var region = ClipRect(Pad(cover, 3), new Rectangle(0, 0, bounds.Width, bounds.Height));
        if (region.Width == 0 || !Contains(region, home))
        {
            return (default!, default, default, false);
        }
        // Only the wall near the opening matters to the funnel.
        k = k with { Walled = [.. k.Walled.Where(c => Contains(region, c))] };
        return (k, region, home, true);
    }

    /// <summary>The opening LayoutKillbox and KillboxOpening read: the anchor with the whole ring as its walled cells, the killbox rectangle and the approach legs.</summary>
    private static (DefenseKillbox Killbox, Rectangle Rect, IReadOnlyList<Rectangle> Approaches, bool Ok) LayoutOpening(LayoutPlan plan)
    {
        var k = new DefenseKillbox();
        var killbox = default(Rectangle);
        var approaches = new List<Rectangle>();
        var (node, nodeFound) = plan.Killbox();
        if (nodeFound)
        {
            killbox = node!.Footprint.Bounds();
        }
        foreach (var a in plan.LiveSites(NodeKind.Approach))
        {
            approaches.Add(a.Footprint.Bounds());
        }
        var (ring, ringFound) = plan.Ring();
        if (ringFound)
        {
            k = k with { Walled = ring!.Footprint.Cells() };
        }
        if (killbox.Width == 0)
        {
            return (new DefenseKillbox(), default, [], false);
        }
        // The straight approach leg is 3 wide and ends one cell outside the opening's outer face; the killbox
        // starts PerimeterThick cells in.
        var found = false;
        foreach (var a in approaches)
        {
            foreach (var d in Directions)
            {
                var (across, along) = (a.Width, a.Height);
                if (d.X != 0)
                {
                    (across, along) = (a.Height, a.Width);
                }
                if (across != 3 || along < 1)
                {
                    continue;
                }
                // The leg's end nearest the opening, centred across.
                var end = new Cell(a.X + (a.Width / 2), a.Z + (a.Height / 2));
                if (d == new Cell(1, 0))
                {
                    end = end with { X = a.X + a.Width - 1 };
                }
                else if (d == new Cell(-1, 0))
                {
                    end = end with { X = a.X };
                }
                else if (d == new Cell(0, 1))
                {
                    end = end with { Z = a.Z + a.Height - 1 };
                }
                else if (d == new Cell(0, -1))
                {
                    end = end with { Z = a.Z };
                }
                var entry = AddCell(end, d);
                if (Contains(killbox, AddCell(entry, Scale(d, PerimeterThick))))
                {
                    k = k with { Entry = entry, Toward = RotationOf(d), Width = 3, Depth = killbox.Height };
                    found = true;
                    if (d.X != 0)
                    {
                        k = k with { Depth = killbox.Width };
                    }
                }
            }
        }
        if (!found)
        {
            return (new DefenseKillbox(), default, [], false);
        }
        return (k, killbox, approaches, true);
    }

    /// <summary>
    /// Cuts the plan's wall into sections of about 60 blocks, nearest the killbox first: walls, with a door at
    /// each end of every gate and the cell between left open, an airlock. The geothermal site's shell, when the
    /// plan holds one, is its own section with a doorway of doors facing the core. A wall on water stands on
    /// bridge, laid by a section of its own just before the wall's; a wall on light footing or on a plain Bridge
    /// is WoodLog. Other stuff is left empty; the planner fills in the stone at admission.
    /// </summary>
    public static IReadOnlyList<PerimeterSection> PerimeterSections(LayoutPlan plan, string wall, string door, string bridge)
    {
        var gates = new HashSet<Cell>();
        var airlock = new HashSet<Cell>();
        var light = new HashSet<Cell>();
        var bridged = new HashSet<Cell>();
        var woodInner = new HashSet<Cell>(); // light-footing wall cells: wood to start
        IReadOnlyList<Rectangle> runs = [];
        var killbox = default(Rectangle);
        var geothermal = default(Rectangle);
        var gateNode = new Dictionary<Cell, string>();
        foreach (var g in plan.LiveSites(NodeKind.Gate))
        {
            // An airlock: a door on each face of the wall, the cells between left open, enclosed by the wall beside them.
            var ends = g.Footprint.Bounds().Width;
            if (g.Footprint.Bounds().Height > ends)
            {
                ends = g.Footprint.Bounds().Height;
            }
            var cells = g.Footprint.Cells();
            for (var i = 0; i < cells.Count; i++)
            {
                gateNode[cells[i]] = g.Node;
                if (i == 0 || i == ends - 1)
                {
                    gates.Add(cells[i]);
                }
                else
                {
                    airlock.Add(cells[i]);
                }
            }
        }
        var ringID = string.Empty;
        var (ringSite, ringFound) = plan.Ring();
        if (ringFound)
        {
            ringID = ringSite!.Node;
            runs = RegionRects(ringSite.Footprint);
            foreach (var c in ringSite.Ring!.Light.Cells())
            {
                woodInner.Add(c);
            }
            foreach (var c in ringSite.Ring.Bridged.Cells())
            {
                bridged.Add(c);
            }
        }
        var (killboxSite, killboxFound) = plan.Killbox();
        if (killboxFound)
        {
            killbox = killboxSite!.Footprint.Bounds();
        }
        var geothermalID = string.Empty;
        var nth = 0;
        foreach (var r in plan.Reservations)
        {
            if (r.Kind != ReservationKind.Geothermal)
            {
                continue;
            }
            nth++;
            if (geothermal.Width == 0)
            {
                geothermal = r.Area.Bounds();
                geothermalID = GeothermalNodeId(nth);
            }
        }
        var centre = new Cell(killbox.X + (killbox.Width / 2), killbox.Z + (killbox.Height / 2));
        var pieces = new List<(Rectangle Area, long Dist)>();
        var ringRect = default(Rectangle);
        foreach (var r in runs)
        {
            ringRect = UnionRect(ringRect, r);
            var horizontal = r.Width >= r.Height;
            var length = horizontal ? r.Width : r.Height;
            for (var off = 0; off < length; off += PerimeterSectionRun)
            {
                var n = Math.Min(PerimeterSectionRun, length - off);
                var a = horizontal ? new Rectangle(r.X + off, r.Z, n, r.Height) : new Rectangle(r.X, r.Z + off, r.Width, n);
                var mid = new Cell(a.X + (a.Width / 2), a.Z + (a.Height / 2));
                pieces.Add((a, SquaredDistance(mid, centre)));
            }
        }
        pieces = pieces.SortedBy((a, b) => a.Dist < b.Dist || (a.Dist == b.Dist && (a.Area.X < b.Area.X || (a.Area.X == b.Area.X && a.Area.Z < b.Area.Z))));
        var output = new List<PerimeterSection>();
        PerimeterSection Next() =>
            new() { Name = new DefenseTierName(string.Create(CultureInfo.InvariantCulture, $"{TierPerimeterPrefix}{output.Count:D2}")), Nodes = [ringID] };
        foreach (var p in pieces)
        {
            var under = Next();
            foreach (var c in RectCells(p.Area))
            {
                if (!bridged.Contains(c))
                {
                    continue;
                }
                under.Buildings.Add(new Building(bridge, c, Rotation.North, string.Empty));
            }
            if (under.Buildings.Count > 0)
            {
                output.Add(under);
            }
            var section = Next();
            foreach (var c in RectCells(p.Area))
            {
                if (airlock.Contains(c))
                {
                    continue;
                }
                var def = wall;
                var stuff = string.Empty;
                if (gates.Contains(c))
                {
                    def = door;
                }
                if (light.Contains(c) || (woodInner.Contains(c) && !gates.Contains(c)) || (bridged.Contains(c) && bridge == PerimeterBridge))
                {
                    stuff = PerimeterLightStuff;
                }
                section.Buildings.Add(new Building(def, c, Rotation.North, stuff));
                if (gateNode.TryGetValue(c, out var id) && id != string.Empty && !section.Nodes.Contains(id))
                {
                    section.Nodes.Add(id);
                }
            }
            if (section.Buildings.Count > 0)
            {
                output.Add(section);
            }
        }
        if (geothermal.Width > 2 * GeothermalShell && geothermal.Height > 2 * GeothermalShell)
        {
            var gen = Pad(geothermal, -GeothermalShell);
            // The doorway crosses the shell on the side facing the ring's centre (the core), mid-side.
            var core = new Cell(ringRect.X + (ringRect.Width / 2), ringRect.Z + (ringRect.Height / 2));
            var mid = new Cell(gen.X + (gen.Width / 2), gen.Z + (gen.Height / 2));
            int dx = core.X - mid.X, dz = core.Z - mid.Z;
            var doorway = new HashSet<Cell>();
            for (var t = 1; t <= GeothermalShell; t++)
            {
                var c = mid;
                if (Math.Abs(dx) >= Math.Abs(dz) && dx >= 0)
                {
                    c = c with { X = gen.X + gen.Width - 1 + t };
                }
                else if (Math.Abs(dx) >= Math.Abs(dz))
                {
                    c = c with { X = gen.X - t };
                }
                else if (dz >= 0)
                {
                    c = c with { Z = gen.Z + gen.Height - 1 + t };
                }
                else
                {
                    c = c with { Z = gen.Z - t };
                }
                doorway.Add(c);
            }
            var s = new PerimeterSection { Name = TierGeothermal, Nodes = [geothermalID] };
            foreach (var c in RectCells(geothermal))
            {
                if (Contains(gen, c))
                {
                    continue;
                }
                s.Buildings.Add(new Building(doorway.Contains(c) ? door : wall, c, Rotation.North, string.Empty));
            }
            output.Add(s);
        }
        return output;
    }

    /// <summary>
    /// One section per moisture pump site: the pump and the conduit run bringing a transmitter within connector
    /// reach, chained over the ring's interior clear of the killbox. A run laid for an earlier pump carries the
    /// later ones. Nothing without a transmitter to join: an unpowered pump dries nothing.
    /// </summary>
    public static IReadOnlyList<PerimeterSection> PerimeterPumps(LayoutPlan plan, string pump, string conduit, IReadOnlyList<Cell> transmitters)
    {
        var killbox = default(Rectangle);
        var sites = new List<Cell>();
        var ids = new List<string>();
        var (killboxSite, killboxFound) = plan.Killbox();
        if (killboxFound)
        {
            killbox = killboxSite!.Footprint.Bounds();
        }
        foreach (var p in plan.LiveSites(NodeKind.Pump))
        {
            sites.Add(p.Origin);
            ids.Add(p.Node);
        }
        if (sites.Count == 0 || transmitters.Count == 0)
        {
            return [];
        }
        var (wi, _) = PlanInterior(plan, 0);
        var allowed = new Dictionary<Cell, bool>();
        var carry = new HashSet<Cell>();
        foreach (var c in wi.Cells())
        {
            allowed[c] = !Contains(killbox, c);
        }
        foreach (var c in sites)
        {
            allowed[c] = false;
        }
        foreach (var c in transmitters)
        {
            allowed[c] = true;
            carry.Add(c);
        }
        var output = new List<PerimeterSection>();
        for (var i = 0; i < sites.Count; i++)
        {
            var site = sites[i];
            var (chain, ok) = new DefenseSite().ConduitChain(site, carry, allowed);
            if (!ok)
            {
                continue;
            }
            var s = new PerimeterSection { Name = new DefenseTierName(string.Create(CultureInfo.InvariantCulture, $"{TierPumpPrefix}{output.Count:D2}")), Nodes = [ids[i]] };
            s.Buildings.Add(new Building(pump, site, Rotation.North, string.Empty));
            foreach (var c in chain)
            {
                s.Buildings.Add(new Building(conduit, c, Rotation.North, string.Empty));
                carry.Add(c);
            }
            output.Add(s);
        }
        return output;
    }

    /// <summary>
    /// One section for the fence cells across the killbox opening (NodeFence): a Fence is PassThroughOnly,
    /// stopping only roamers, so the lane stays the raiders' way in.
    /// </summary>
    public static IReadOnlyList<PerimeterSection> FenceSections(LayoutPlan plan, string fence, string stuff)
    {
        var output = new List<PerimeterSection>();
        foreach (var f in plan.LiveSites(NodeKind.Fence))
        {
            var s = new PerimeterSection { Name = new DefenseTierName(string.Create(CultureInfo.InvariantCulture, $"{TierFencePrefix}{output.Count:D2}")), Nodes = [f.Node] };
            foreach (var c in f.Footprint.Cells())
            {
                s.Buildings.Add(new Building(fence, c, Rotation.North, stuff));
            }
            output.Add(s);
        }
        return output;
    }

    public static Rectangle UnionRect(Rectangle a, Rectangle b)
    {
        if (a.Width == 0)
        {
            return b;
        }
        if (b.Width == 0)
        {
            return a;
        }
        int x0 = Math.Min(a.X, b.X), z0 = Math.Min(a.Z, b.Z);
        int x1 = Math.Max(a.X + a.Width, b.X + b.Width), z1 = Math.Max(a.Z + a.Height, b.Z + b.Height);
        return new Rectangle(x0, z0, x1 - x0, z1 - z0);
    }
}
