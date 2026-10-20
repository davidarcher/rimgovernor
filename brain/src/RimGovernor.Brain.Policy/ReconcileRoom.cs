using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// The build side of the reconciler: one library an owning concern calls for its own room. The owner decides
// which room and when; the reconciler decides how: it diffs the room's ring, floor and furniture against the
// ground and reports the operations ready now, installing from packed stock first (Stock) and building on site
// only when none is stored. The plan-wide clear side (PlannedGroundStep) owns the ring's removals, so those
// kinds are not the owner's.
public sealed partial record LayoutPlan
{
    /// <summary>
    /// g with the natural rock on the rings of the plan's rooms counted as wall, except where the plan puts a
    /// door: rock walls a dug room as it stands and is no wall to raise.
    /// </summary>
    public GroundCensus GroundWithRock(GroundCensus g, IReadOnlyList<Cell> rock)
    {
        var doors = new HashSet<Cell>();
        foreach (var r in Rooms)
        {
            foreach (var d in this.ShellDoors(r))
            {
                doors.Add(d);
            }
        }
        var output = new GroundCensus
        {
            Walls = new HashSet<Cell>(g.Walls),
            Doors = g.Doors,
            Fences = g.Fences,
            Gates = g.Gates,
            Stuff = g.Stuff,
            Vents = g.Vents,
            Rock = [],
        };
        foreach (var c in rock)
        {
            if (!doors.Contains(c))
            {
                output.Walls.Add(c);
                output.Rock.Add(c);
            }
        }
        return output;
    }
}

public static partial class ReconcileRules
{
    /// <summary>
    /// The room's next operations: every ready operation of Reconcile except the ring's removals (door and
    /// wall out, roof off), batched by kind in table order. Empty when the room matches or waits on a
    /// prerequisite.
    /// </summary>
    public static IReadOnlyList<Operation> ReconcileRoom(ReconcileInput input) => ReconcileRoomHolds(input).Operations;

    /// <summary>ReconcileRoom with the foreign things the room leaves standing.</summary>
    public static (IReadOnlyList<Operation> Operations, IReadOnlyList<ReconcileHold> Holds) ReconcileRoomHolds(ReconcileInput input)
    {
        var output = new List<Operation>();
        var rec = Reconcile(input);
        foreach (var op in rec.Ready)
        {
            if (op.Kind is OpKind.DoorOut or OpKind.WallOut or OpKind.RoofOff)
            {
                continue;
            }
            output.Add(op);
        }
        return (output, rec.Holds);
    }

    /// <summary>
    /// The census rows an owner reconciles its room against, the full list: the ring, frames and stand-ins, the
    /// template's standing pieces, the buildings forbidden finds (packed or taken down by the reconcile) and
    /// every other player building wholly inside the interior that the template does not list and
    /// <paramref name="carried"/> does not carry over: stale furniture, which the reconcile removes
    /// (FurnitureOut, Pack, PackInUse). Anything else the room holds is left alone.
    /// </summary>
    public static IReadOnlyList<ClearanceTarget> OwnerRows(PlannedRoom room, IReadOnlyList<ClearanceTarget> rows, IReadOnlyList<WantedPiece> template, Func<string, bool>? forbidden, Func<string, bool> carried)
    {
        var stale = new HashSet<ClearanceTarget>(StaleInterior(room, rows, template, forbidden, carried), ReferenceEqualityComparer.Instance);
        var output = new List<ClearanceTarget>();
        foreach (var row in rows)
        {
            var keep = stale.Contains(row) || !row.Player || row.Class == "ancient_wall_door" || row.DefName.StartsWith("Frame_", StringComparison.Ordinal) || ClearanceGroundRules.StandInBed(row) || (forbidden is not null && forbidden(row.DefName));
            foreach (var p in template)
            {
                keep = keep || (p.DefName == row.DefName && p.Minimum == row.Minimum && p.Maximum == row.Maximum);
            }
            if (keep)
            {
                output.Add(row);
            }
        }
        return output;
    }

    /// <summary>
    /// The interior furniture an owner reconciling against its whole list removes: each player building wholly
    /// inside the room's interior that no piece of <paramref name="template"/> lists (matched as Reconcile
    /// matches, one row per piece) and that is not carried over. Walls, doors, frames, stand-in beds and what
    /// the owner forbids are the ring's or already the owner's, not stale furniture.
    /// <paramref name="carried"/> names the defs other lanes stand in owned rooms (art, lamps, the party spot,
    /// comfort pieces): listed as standing, never stale.
    /// </summary>
    public static IReadOnlyList<ClearanceTarget> StaleInterior(PlannedRoom room, IReadOnlyList<ClearanceTarget> rows, IReadOnlyList<WantedPiece> template, Func<string, bool>? forbidden, Func<string, bool> carried)
    {
        var matched = new bool[template.Count];
        var output = new List<ClearanceTarget>();
        foreach (var row in rows.OrderBy(r => r.EntityId, StringComparer.Ordinal))
        {
            if (row.EnclosesRoom || !InteriorFurniture(room, row) || (forbidden is not null && forbidden(row.DefName)))
            {
                continue;
            }
            var hit = -1;
            for (var i = 0; i < template.Count && hit < 0; i++)
            {
                var p = template[i];
                if (!matched[i] && p.DefName == row.DefName && p.Minimum == row.Minimum && p.Maximum == row.Maximum)
                {
                    hit = i;
                }
            }
            if (hit >= 0)
            {
                matched[hit] = true;
            }
            else if (!carried(row.DefName))
            {
                output.Add(row);
            }
        }
        return output;
    }

    /// <summary>
    /// Whether row is furniture standing wholly inside room's interior: a player building that is no ancient wall
    /// or door, no frame and no stand-in bed, with no cell on the ring. The clear side leaves such rows to the
    /// owner (InteriorFurnitureLeft) and the owner removes the unlisted ones (StaleInterior).
    /// </summary>
    internal static bool InteriorFurniture(PlannedRoom room, ClearanceTarget row) =>
        row.Player
        && row.Class != "ancient_wall_door"
        && !row.DefName.StartsWith("Frame_", StringComparison.Ordinal)
        && !ClearanceGroundRules.StandInBed(row)
        && PlannedRules.RectCells(new Rectangle(row.Minimum.X, row.Minimum.Z, row.Maximum.X - row.Minimum.X + 1, row.Maximum.Z - row.Minimum.Z + 1)).All(room.Region.Contains);

    /// <summary>
    /// The definitions other lanes stand in owned rooms and the room's list carries over, so an owner never
    /// removes them as stale: the lamps, the party spot, the plant pot, the dining and recreation comfort pieces
    /// (<paramref name="comfort"/>, when read), every sculpture the catalog makes, and the power and climate
    /// infrastructure the power and climate planners run through rooms (conduits, generators, batteries, vents,
    /// the temperature planner's campfire and passive cooler), which is no furniture.
    /// </summary>
    public static Func<string, bool> CarriedOver(IEnumerable<string> lamps, ComfortObservation? comfort, ItemFacts items)
    {
        var defs = new HashSet<string>(lamps, StringComparer.Ordinal)
        {
            PartySpotRules.PartySpotDefinition,
            RoomQualityRules.PlantPotDefinition,
            LayoutPowerDeriveRules.PowerConduitDefinition,
            ClimateRules.ClimateDefs().VentDef,
            Wire.Name(TemperatureMethod.Heat),
            Wire.Name(TemperatureMethod.Cool),
        };
        defs.UnionWith(PowerRules.PowerFamilyDefinitions());
        if (comfort is not null)
        {
            defs.UnionWith(comfort.Furniture.Definitions());
            defs.UnionWith(comfort.WatchBuildings);
            defs.Add(comfort.RecreationFoothold);
        }
        return def => defs.Contains(def) || items.SculptureSize(def, out _);
    }

    /// <summary>The rectangle a planned room's ring and interior cover, the ground an owner reads the census on.</summary>
    public static Rectangle RoomGround(this PlannedRoom r) =>
        r.Unfenced() ? r.Region.Bounds() : ClearanceGroundRules.RoomGround(r.Region.Bounds());

    /// <summary>The template piece for p: its slot's cells, size and rotation.</summary>
    public static WantedPiece Wanted(this InteriorPiece p) => new()
    {
        DefName = p.Def,
        Minimum = new Cell(p.Rect.X, p.Rect.Z),
        Maximum = new Cell(p.Rect.X + p.Rect.Width - 1, p.Rect.Z + p.Rect.Height - 1),
        Size = p.Size,
        Rot = p.Rot,
    };

    /// <summary>
    /// The cells a planned room's ring and interior cover: the region and its wall ring, so an L's notch is no
    /// ground; an unfenced room's block.
    /// </summary>
    internal static Region GroundRegion(this PlannedRoom r)
    {
        if (r.Unfenced())
        {
            return r.Region;
        }
        var cells = new List<Cell>(r.Region.Cells());
        cells.AddRange(r.Region.Walls().Cells());
        return RegionRules.RegionOf(cells);
    }
}
