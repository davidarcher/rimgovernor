namespace RimGovernor.Brain.Policy;

// Soil build costs per cell: rich soil costs more than plain soil, which costs more than anything else (bare
// ground, rock). Rich soil (fertility above ZoneRichFertility, e.g. 140%) is the best farmland on the map, so a
// room over it costs 50x plain soil: still a cost, not a ban, but only a site with no other ground pays it.
public static partial class PolicyConstants
{
    /// <summary>The deepest room interior the siting seeds allow for.</summary>
    internal const int CoreMaxDepth = 7;

    /// <summary>The half height of the cross-section a siting seed must have free about its row: one lane cell, the deepest room and a wall on each side.</summary>
    internal const int CoreHalf = 1 + CoreMaxDepth + 2;

    internal const int SoilCostRich = 100;
    internal const int SoilCostNormal = 2;
    internal const int SoilCostOther = 0;
}

/// <summary>Names which core cells are obstacles. Obstacles are geometry first and cost second: ObstacleLevels tries the strictest map, then fewer, and the last level keeps no obstacle at all, so the soil cost prices the plan when nothing else places every room.</summary>
public enum ObstacleLevel
{
    /// <summary>Rich soil, ore rock and every field zone cell.</summary>
    All,

    /// <summary>Rich soil and ore rock only; plain fields may be built on.</summary>
    Rich,

    /// <summary>No obstacle; rich soil is a cost (SoilCostRich), not a ban.</summary>
    None,
}

/// <summary>
/// The survey's ground as the embedder and the tiler read it: the cells a room may stand on (the core
/// candidates, ZoneCore), the rock a room there is dug into (ZoneMining), and each cell's soil cost. The other
/// zones give way to the core wherever they overlap it.
/// </summary>
internal sealed record BuildGround
{
    public HashSet<Cell> Core { get; init; } = [];

    public HashSet<Cell> Rock { get; init; } = [];

    /// <summary>Each surveyed cell's build cost; empty costs nothing.</summary>
    public IReadOnlyDictionary<Cell, int> Soil { get; init; } = new Dictionary<Cell, int>();

    /// <summary>Takes reserved sites out of the core candidates.</summary>
    public static BuildGround Create(IReadOnlyList<LayoutZone> zones, IReadOnlyList<LayoutReservation> reserved)
    {
        var core = new HashSet<Cell>();
        var rock = new HashSet<Cell>();
        foreach (var z in zones)
        {
            HashSet<Cell> set;
            switch (z.Kind)
            {
                case ZoneKind.Core:
                    set = core;
                    break;
                case ZoneKind.Mining:
                    set = rock;
                    break;
                default:
                    continue;
            }
            foreach (var r in z.Runs)
            {
                for (var x = r.X; x < r.X + r.Length; x++)
                {
                    set.Add(new Cell(x, r.Z));
                }
            }
        }
        foreach (var r in reserved)
        {
            foreach (var c in r.Area.Cells())
            {
                core.Remove(c);
            }
        }
        return new BuildGround { Core = core, Rock = rock };
    }

    /// <summary>Gives the ground a per-cell build cost from the survey's fertility.</summary>
    public BuildGround WithSoil(MapSurvey s) => this with { Soil = SurveySoil(s) };

    /// <summary>Each surveyed cell's build cost.</summary>
    public static Dictionary<Cell, int> SurveySoil(MapSurvey s)
    {
        var soil = new Dictionary<Cell, int>(s.Cells.Count);
        foreach (var c in s.Cells)
        {
            if (c.Rock)
            {
                soil[c.Cell] = SoilCostOther;
            }
            else if (c.Fertility > ZoneRichFertility)
            {
                soil[c.Cell] = SoilCostRich;
            }
            else if (c.Fertility >= ZoneFieldFertility)
            {
                soil[c.Cell] = SoilCostNormal;
            }
            else
            {
                soil[c.Cell] = SoilCostOther;
            }
        }
        return soil;
    }

    /// <summary>Sums the build cost of r's cells.</summary>
    public int SoilCost(Rectangle r)
    {
        var n = 0;
        for (var x = r.X; x < r.X + r.Width; x++)
        {
            for (var z = r.Z; z < r.Z + r.Height; z++)
            {
                n += Soil.GetValueOrDefault(new Cell(x, z));
            }
        }
        return n;
    }

    /// <summary>Reports the whole cross-section at x around row z free: half a hallway plus the deepest room and its walls on both sides.</summary>
    public bool Column(int x, int z)
    {
        for (var dz = -CoreHalf; dz <= CoreHalf; dz++)
        {
            if (!Core.Contains(new Cell(x, z + dz)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Picks the start of the siting: the column-fitting cell nearest the core candidates' centroid.</summary>
    public (Cell Cell, bool Ok) Seed()
    {
        long sx = 0, sz = 0;
        foreach (var c in Core)
        {
            sx += c.X;
            sz += c.Z;
        }
        long n = Core.Count;
        var cx = (int)(sx / n);
        var cz = (int)(sz / n);
        Cell best = default;
        var found = false;
        long bestD = -1;
        foreach (var c in Core)
        {
            long dx = c.X - cx;
            long dz = c.Z - cz;
            var d = (dx * dx) + (dz * dz);
            if (found && (d > bestD || (d == bestD && (c.Z > best.Z || (c.Z == best.Z && c.X > best.X)))))
            {
                continue;
            }

            // The seed is a hallway end: its one-cell cap needs core too.
            if (Column(c.X - 1, c.Z) && Column(c.X, c.Z) && Column(c.X + 1, c.Z))
            {
                (best, found, bestD) = (c, true, d);
            }
        }
        return (best, found);
    }

    /// <summary>Reports whether any interior cell of r is rock.</summary>
    public bool Dug(PlannedRoom r)
    {
        var inside = r.Region.Bounds();
        for (var x = inside.X; x < inside.X + inside.Width; x++)
        {
            for (var z = inside.Z; z < inside.Z + inside.Height; z++)
            {
                if (Rock.Contains(new Cell(x, z)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// The cells of Core at level that a room keeps off: rich soil, the ore the plan will not dig, the field
    /// zones (soil worth farming) and, already out of the core, the reserved sites. A rich patch inside the best
    /// ground is left open (a courtyard) rather than built over at SoilCostRich. Zones supplies the field and ore
    /// cells; Soil the rich cells.
    /// </summary>
    public HashSet<Cell> CoreObstacles(IReadOnlyList<LayoutZone> zones, ObstacleLevel level) => CoreObstaclesFor(zones, level, true);

    /// <summary>CoreObstacles with the field ban optional: fields are nodes of the station graph, placed by the embedder and sharing occupancy with the rooms (#2920), so the embedder asks for none.</summary>
    public HashSet<Cell> CoreObstaclesFor(IReadOnlyList<LayoutZone> zones, ObstacleLevel level, bool banFields)
    {
        var obs = new HashSet<Cell>();
        if (level == ObstacleLevel.None)
        {
            return obs;
        }
        foreach (var c in Core)
        {
            if (Soil.GetValueOrDefault(c) == SoilCostRich)
            {
                obs.Add(c);
            }
        }
        foreach (var z in zones)
        {
            if ((z.Kind == ZoneKind.Mining && z.Ore) || (z.Kind == ZoneKind.Field && level == ObstacleLevel.All && banFields))
            {
                foreach (var r in z.Runs)
                {
                    for (var x = r.X; x < r.X + r.Length; x++)
                    {
                        var c = new Cell(x, r.Z);
                        if (Core.Contains(c))
                        {
                            obs.Add(c);
                        }
                    }
                }
            }
        }
        return obs;
    }

    /// <summary>A copy of the ground whose core lacks obs.</summary>
    public BuildGround Without(IReadOnlySet<Cell> obs) => this with { Core = [.. Core.Where(c => !obs.Contains(c))] };
}

internal static class ObstacleLevels
{
    /// <summary>The order siting tries the levels in.</summary>
    public static readonly IReadOnlyList<ObstacleLevel> All = [ObstacleLevel.All, ObstacleLevel.Rich, ObstacleLevel.None];
}
