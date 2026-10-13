namespace RimGovernor.Brain.Policy;

public sealed partial record LayoutPlan
{
    /// <summary>Every cell of the plan's field zones.</summary>
    public HashSet<Cell> FieldCells()
    {
        var output = new HashSet<Cell>();
        foreach (var z in Zones)
        {
            if (z.Kind != PolicyFunctions.ZoneField)
            {
                continue;
            }
            foreach (var r in z.Runs)
            {
                for (var x = r.X; x < r.X + r.Length; x++)
                {
                    output.Add(new Cell(x, r.Z));
                }
            }
        }
        return output;
    }
}

/// <summary>Ports <c>layout_zoning.go</c>: whole-map zoning, every surveyed cell classified from terrain.</summary>
public static partial class PolicyFunctions
{
    /// <summary>Ground the core may grow over.</summary>
    public static ZoneKind ZoneCore => ZoneKind.Core;

    /// <summary>The least fertility a field cell needs (plain soil).</summary>
    private const double ZoneFieldFertility = 1.0;

    /// <summary>The fewest cells a fertile patch needs to be a field; smaller patches are pasture.</summary>
    private const int ZoneFieldMin = 16;

    /// <summary>
    /// Classifies every surveyed cell into whole-map zones, in order: core candidates, fields (one zone per
    /// 4-connected fertile patch, largest first), pasture, mining (ore first, then the other rock), wood, no-go. Kinds
    /// with no cells are left out. Zones overlap on purpose.
    /// </summary>
    public static List<LayoutZone> Zone(MapSurvey s)
    {
        int w = s.Bounds.Width, h = s.Bounds.Height;
        var zones = new List<LayoutZone>();
        if (w < 1 || h < 1)
        {
            return zones;
        }
        var cells = new SurveyCell?[w * h];
        foreach (var c in s.Cells)
        {
            if (c.Cell.X >= 0 && c.Cell.Z >= 0 && c.Cell.X < w && c.Cell.Z < h)
            {
                cells[(c.Cell.Z * w) + c.Cell.X] = c;
            }
        }
        var e = LayoutEdgeMargin;
        bool NoGo(int i)
        {
            var c = cells[i];
            int x = i % w, z = i / w;
            return (c is not null && (c.Hazard || (!c.Rock && c.Footing != Footing.Firm))) || x < e || z < e || x >= w - e || z >= h - e;
        }
        Func<int, bool> Has(Func<SurveyCell, bool> pred) => i => cells[i] is { } c && !NoGo(i) && pred(c);
        var soil = Has(c => !c.Rock && c.Walkable && c.Fertility >= ZoneFieldFertility);
        var fields = Components(w, h, soil);
        var inField = new bool[cells.Length];
        void Add(ZoneKind kind, Func<int, bool> inZone)
        {
            var runs = ZoneRuns(w, h, inZone);
            if (runs.Count > 0)
            {
                zones.Add(new LayoutZone { Kind = kind, Runs = runs });
            }
        }
        void AddOre(Func<int, bool> inZone)
        {
            var runs = ZoneRuns(w, h, inZone);
            if (runs.Count > 0)
            {
                zones.Add(new LayoutZone { Kind = ZoneMining, Runs = runs, Ore = true });
            }
        }
        Add(ZoneCore, Has(c => (c.Rock || c.Walkable || c.Ruin) && !c.Prop));
        foreach (var f in fields)
        {
            if (f.Count < ZoneFieldMin)
            {
                break;
            }
            foreach (var i in f)
            {
                inField[i] = true;
            }
            var set = f.ToHashSet();
            Add(ZoneField, i => set.Contains(i));
        }
        Add(ZonePasture, Has(c => !c.Rock && c.Walkable && c.Fertility > 0 && !inField[(c.Cell.Z * w) + c.Cell.X]));
        AddOre(Has(c => c.Rock && c.Ore));
        Add(ZoneMining, Has(c => c.Rock && !c.Ore));
        Add(ZoneWood, Has(c => c.Tree));
        Add(ZoneNoGo, i => cells[i] is not null && NoGo(i));
        return zones;
    }

    /// <summary>The 4-connected components of the cells inZone accepts, largest first (ties by first cell).</summary>
    private static List<List<int>> Components(int w, int h, Func<int, bool> inZone)
    {
        var seen = new bool[w * h];
        var output = new List<List<int>>();
        for (var start = 0; start < w * h; start++)
        {
            if (seen[start] || !inZone(start))
            {
                continue;
            }
            seen[start] = true;
            var comp = new List<int> { start };
            for (var k = 0; k < comp.Count; k++)
            {
                var i = comp[k];
                int x = i % w, z = i / w;
                foreach (var (nx, nz) in new[] { (x - 1, z), (x + 1, z), (x, z - 1), (x, z + 1) })
                {
                    if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                    {
                        continue;
                    }
                    var j = (nz * w) + nx;
                    if (!seen[j] && inZone(j))
                    {
                        seen[j] = true;
                        comp.Add(j);
                    }
                }
            }
            output.Add(comp);
        }

        // stable insertion sort by size, descending
        for (var a = 1; a < output.Count; a++)
        {
            for (var b = a; b > 0 && output[b].Count > output[b - 1].Count; b--)
            {
                (output[b], output[b - 1]) = (output[b - 1], output[b]);
            }
        }
        return output;
    }

    /// <summary>Encodes the cells inZone accepts as row runs, row by row.</summary>
    private static List<RowRun> ZoneRuns(int w, int h, Func<int, bool> inZone)
    {
        var runs = new List<RowRun>();
        for (var z = 0; z < h; z++)
        {
            for (var x = 0; x < w; x++)
            {
                if (!inZone((z * w) + x))
                {
                    continue;
                }
                var start = x;
                while (x + 1 < w && inZone((z * w) + x + 1))
                {
                    x++;
                }
                runs.Add(new RowRun(z, start, x - start + 1));
            }
        }
        return runs;
    }

    /// <summary>The fertility above which soil is rich.</summary>
    public const double ZoneRichFertility = 1.0;
}
