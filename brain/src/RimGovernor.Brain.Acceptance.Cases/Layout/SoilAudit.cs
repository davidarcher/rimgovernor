using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>layout/rich-soil</c> audit over a recorded layout plan, the map survey it was planned on and the colony's
/// growing zones (Go <c>soil_audit.go</c>). Each list holds the violations found (capped), empty when the assertion holds.
/// </summary>
internal sealed class LayoutSoilAudit
{
    /// <summary>The fertility above which soil is rich.</summary>
    private const double RichFertility = 1.0;

    /// <summary>The fraction of the map's rich cells planned rooms may cover: the planner prices rich soil as a cost, not a ban, so a small overlap is allowed.</summary>
    private const double RichOverlapBudget = 0.02;

    private const int AuditCap = 8;

    private static readonly Cell[] Four = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1)];

    internal int RichCells { get; private set; }

    internal int RichBuiltN { get; private set; }

    internal double RichOverlap { get; private set; }

    internal int Patches { get; private set; }

    internal int WallCells { get; private set; }

    internal int CropZones { get; private set; }

    internal List<string> RichBuilt { get; } = [];

    internal List<string> PatchSplit { get; } = [];

    internal List<string> CropGaps { get; } = [];

    /// <summary>The audit as the report's JSON (Go's struct tags; an empty violation list is omitted).</summary>
    internal JsonObject ToJson()
    {
        var row = new JsonObject
        {
            ["rich_cells"] = RichCells,
            ["rich_built_cells"] = RichBuiltN,
            ["rich_overlap_fraction"] = RichOverlap,
            ["patches"] = Patches,
            ["wall_cells"] = WallCells,
            ["crop_zones"] = CropZones,
        };
        AddList(row, "rich_built", RichBuilt);
        AddList(row, "patch_split", PatchSplit);
        AddList(row, "crop_gaps", CropGaps);
        return row;
    }

    /// <summary>The first failed assertion, null when all hold.</summary>
    internal string? Error()
    {
        if (RichOverlap > RichOverlapBudget)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"planned rooms cover {RichOverlapText()}, over the {RichOverlapBudget * 100:F0}% budget: [{string.Join(' ', RichBuilt)}]");
        }

        foreach (var (what, list) in new[] { ("fertile patch not one block", PatchSplit), ("crop zones in a patch not adjacent", CropGaps) })
        {
            if (list.Count > 0)
            {
                return $"{what}: [{string.Join(' ', list)}]";
            }
        }

        return null;
    }

    /// <summary>Reports the rich cells rooms cover.</summary>
    internal string RichOverlapText() =>
        string.Create(CultureInfo.InvariantCulture, $"{RichBuiltN} of {RichCells} rich cells ({RichOverlap * 100:F2}%)");

    /// <summary>
    /// Checks the plan and crop zones against the survey:
    /// (1) planned rooms cover at most the rich-cell budget;
    /// (2) each rich patch of a field zone is one block and touches no other, and a rich patch of any footprint (an L,
    /// a plus, a courtyard that is no rectangle) is farmed as one 4-connected block;
    /// (3) the growing zones inside each field zone form one 4-connected block.
    /// </summary>
    internal static LayoutSoilAudit Audit(LayoutPlan plan, MapSurvey survey, IReadOnlyList<IReadOnlyList<Cell>> crops)
    {
        var a = new LayoutSoilAudit();
        var rich = new HashSet<Cell>();
        foreach (var c in survey.Cells)
        {
            if (c.Fertility > RichFertility)
            {
                rich.Add(c.Cell);
            }
        }

        a.RichCells = rich.Count;
        var built = BuiltCells(plan);
        foreach (var c in Sorted(built.Keys))
        {
            if (rich.Contains(c))
            {
                a.RichBuiltN++;
                Note(a.RichBuilt, $"{built[c]} at {c}");
            }
        }

        if (a.RichCells > 0)
        {
            a.RichOverlap = (double)a.RichBuiltN / a.RichCells;
        }

        a.WallCells = plan.WallCells().Count;

        var patches = new List<HashSet<Cell>>();
        var owner = new Dictionary<Cell, int>();
        var zoneOf = new Dictionary<Cell, int>();
        for (var zi = 0; zi < plan.Zones.Count; zi++)
        {
            var z = plan.Zones[zi];
            if (z.Kind != PolicyFunctions.ZoneField || z.Runs.Count == 0)
            {
                continue;
            }

            // The wall takes in rich soil only: each 4-connected rich part of the zone is one patch.
            var left = new HashSet<Cell>();
            foreach (var r in z.Runs)
            {
                for (var x = r.X; x < r.X + r.Length; x++)
                {
                    var c = new Cell(x, r.Z);
                    zoneOf[c] = zi + 1;
                    if (rich.Contains(c))
                    {
                        left.Add(c);
                    }
                }
            }

            foreach (var start in Sorted(left))
            {
                if (!left.Contains(start))
                {
                    continue;
                }

                var p = Flood(start, left);
                foreach (var c in p)
                {
                    owner[c] = patches.Count + 1;
                }

                patches.Add(p);
            }
        }

        a.Patches = patches.Count;
        for (var i = 0; i < patches.Count; i++)
        {
            var p = patches[i];
            var first = Sorted(p)[0];
            if (!Connected(p))
            {
                Note(a.PatchSplit, string.Create(CultureInfo.InvariantCulture, $"field zone {i} at {first} is not one patch"));
            }

            foreach (var c in Sorted(p))
            {
                foreach (var d in Four)
                {
                    var n = Step(c, d);
                    if (owner.TryGetValue(n, out var other) && other != 0 && other != i + 1)
                    {
                        Note(a.PatchSplit, string.Create(CultureInfo.InvariantCulture, $"field zones {i} and {other - 1} touch at {c}"));
                    }
                }
            }
        }

        // A rich patch of any shape (the survey's 4-connected rich cells, an L, a plus or a ring as much as a rectangle)
        // is farmed as one block: the field cells over it form one 4-connected set, so a room cut through it splits it
        // into two and is caught here even inside the overlap budget.
        var remaining = new HashSet<Cell>(rich);
        foreach (var start in Sorted(remaining))
        {
            if (!remaining.Contains(start))
            {
                continue;
            }

            var patch = Flood(start, remaining);
            var farmed = new HashSet<Cell>();
            foreach (var c in patch)
            {
                if (owner.TryGetValue(c, out var o) && o != 0)
                {
                    farmed.Add(c);
                }
            }

            if (!Connected(farmed))
            {
                Note(a.PatchSplit, $"rich patch at {Sorted(patch)[0]} is farmed in more than one block");
            }
        }

        a.CropZones = crops.Count;

        // Crops are laid per field zone, plain soil included.
        var perPatch = new SortedDictionary<int, HashSet<Cell>>();
        foreach (var zone in crops)
        {
            foreach (var c in zone)
            {
                if (zoneOf.TryGetValue(c, out var i) && i != 0)
                {
                    if (!perPatch.TryGetValue(i, out var set))
                    {
                        set = [];
                        perPatch[i] = set;
                    }

                    set.Add(c);
                }
            }
        }

        foreach (var (i, set) in perPatch)
        {
            if (!Connected(set))
            {
                Note(a.CropGaps, string.Create(CultureInfo.InvariantCulture, $"field zone {i - 1}: {set.Count} crop cells in more than one block"));
            }
        }

        return a;
    }

    /// <summary>The cells of the 4-connected component of <paramref name="start"/> in <paramref name="left"/>, which it removes from <paramref name="left"/>.</summary>
    private static HashSet<Cell> Flood(Cell start, HashSet<Cell> left)
    {
        var p = new HashSet<Cell> { start };
        left.Remove(start);
        for (var q = new Queue<Cell>([start]); q.Count > 0;)
        {
            var c = q.Dequeue();
            foreach (var d in Four)
            {
                var n = Step(c, d);
                if (left.Remove(n))
                {
                    p.Add(n);
                    q.Enqueue(n);
                }
            }
        }

        return p;
    }

    /// <summary>Whether <paramref name="set"/> is one 4-connected component.</summary>
    private static bool Connected(HashSet<Cell> set)
    {
        if (set.Count == 0)
        {
            return true;
        }

        var start = set.First();
        var seen = new HashSet<Cell> { start };
        for (var q = new Queue<Cell>([start]); q.Count > 0;)
        {
            var c = q.Dequeue();
            foreach (var d in Four)
            {
                var n = Step(c, d);
                if (set.Contains(n) && seen.Add(n))
                {
                    q.Enqueue(n);
                }
            }
        }

        return seen.Count == set.Count;
    }

    /// <summary>Every cell a planned room (walls included) covers.</summary>
    private static Dictionary<Cell, string> BuiltCells(LayoutPlan plan)
    {
        var output = new Dictionary<Cell, string>();
        foreach (var r in plan.Rooms)
        {
            var inner = r.Region.Bounds();
            foreach (var c in PolicyFunctions.RectangleCells(new Rectangle(inner.X - 1, inner.Z - 1, inner.Width + 2, inner.Height + 2)))
            {
                output[c] = "room " + Wire.Name(r.Role);
            }
        }

        return output;
    }

    private static Cell Step(Cell c, Cell d) => new(c.X + d.X, c.Z + d.Z);

    private static void Note(List<string> list, string text)
    {
        if (list.Count < AuditCap)
        {
            list.Add(text);
        }
    }

    private static void AddList(JsonObject row, string key, List<string> list)
    {
        if (list.Count > 0)
        {
            row[key] = new JsonArray([.. list.Select(t => (JsonNode?)t)]);
        }
    }

    /// <summary>The cells in z-then-x order (Go <c>sortedCells</c>).</summary>
    internal static List<Cell> Sorted(IEnumerable<Cell> cells) => [.. cells.OrderBy(c => c.Z).ThenBy(c => c.X)];
}
