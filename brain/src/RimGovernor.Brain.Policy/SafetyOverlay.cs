namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>The overlay layer the safety assessment draws on.</summary>
    public const string SafetyLayer = "safety";

    /// <summary>
    /// The drawn reach around a holding threat: the serve watch policy's hostile_within, the distance at which
    /// the native supervisor stops a running window for a hostile (DistantThreatCells).
    /// </summary>
    public const int ThreatReachCells = 20;

    /// <summary>The native RaidArrivalState edge margin: a hostile lord whose first pawn spawns this close to the map edge arrived on the ground.</summary>
    public const int RaidEdgeCells = 14;

    private static readonly OverlayHue DangerHue = new(1f, 0.15f, 0.15f);

    private static readonly OverlayHue VetoHue = new(1f, 0.55f, 0.1f);

    /// <summary>
    /// Draws what the bot considers dangerous: a red fill and outline around every threat that holds the
    /// emergency (ThreatHolds), with the raid-arrival edge margin while a raid holds, and an orange outline
    /// over each stack the loot census vetoed as unsafe to haul. One label per connected area names its
    /// causes. Empty (the layer clears) when no threat holds and nothing is vetoed.
    /// </summary>
    public static LayoutOverlay SafetyOverlay(IReadOnlyList<EmergencyThreat> threats, IReadOnlyList<LootItem> loot, Bounds bounds)
    {
        var layers = new List<OverlayLayer>();
        var labels = new List<OverlayLabel>();
        var cause = new Dictionary<Cell, string>();
        var seeds = new List<Cell>();
        var raid = false;
        foreach (var t in threats)
        {
            if (!ThreatHolds(t))
            {
                continue;
            }
            var name = ThreatCause(t);
            raid = raid || name == "raid";
            IReadOnlyList<Cell> cells = t.Cells;
            if (t.Position.TryGetValue(out var pos) && !t.Building())
            {
                cells = [pos];
            }
            foreach (var at in cells)
            {
                if (!InBounds(at, bounds))
                {
                    continue;
                }
                if (cause.TryAdd(at, name))
                {
                    seeds.Add(at);
                }
            }
        }
        var danger = new HashSet<Cell>();
        foreach (var at in seeds)
        {
            for (var z = at.Z - ThreatReachCells; z <= at.Z + ThreatReachCells; z++)
            {
                for (var x = at.X - ThreatReachCells; x <= at.X + ThreatReachCells; x++)
                {
                    var c = new Cell(x, z);
                    if (InBounds(c, bounds))
                    {
                        danger.Add(c);
                    }
                }
            }
        }
        if (danger.Count > 0)
        {
            var runs = CellRuns(SetCells(danger));
            layers.Add(new OverlayLayer { Color = DangerHue.Fill(), Style = OverlayStyle.Fill, Label = "danger", Runs = runs });
            layers.Add(new OverlayLayer { Color = DangerHue.Outline(), Style = OverlayStyle.Outline, Label = "danger", Runs = runs });
            labels.AddRange(AreaLabels(danger, seeds, cause));
        }
        if (raid)
        {
            var band = EdgeBand(bounds, RaidEdgeCells);
            if (band.Count > 0)
            {
                layers.Add(new OverlayLayer { Color = DangerHue.Fill(), Style = OverlayStyle.Fill, Label = "raid edge", Rects = EquatableArray.From(band) });
                layers.Add(new OverlayLayer { Color = DangerHue.Outline(), Style = OverlayStyle.Outline, Label = "raid edge", Rects = EquatableArray.From(band) });
                labels.Add(new OverlayLabel { Text = "raid edge", Cell = new Cell(bounds.Width / 2, RaidEdgeCells / 2) });
            }
        }
        var vetoed = new HashSet<Cell>();
        var vetoSeeds = new List<Cell>();
        var vetoCause = new Dictionary<Cell, string>();
        foreach (var row in loot)
        {
            var at = row.Supply.Cell;
            if (!row.SafetyKnown || row.SafeToHaul || !InBounds(at, bounds) || vetoed.Contains(at))
            {
                continue;
            }
            vetoed.Add(at);
            vetoCause[at] = "unsafe route";
            vetoSeeds.Add(at);
        }
        if (vetoed.Count > 0)
        {
            layers.Add(new OverlayLayer { Color = VetoHue.Outline(), Style = OverlayStyle.Outline, Label = "unsafe route", Runs = CellRuns(SetCells(vetoed)) });
            labels.AddRange(AreaLabels(vetoed, vetoSeeds, vetoCause));
        }
        return new LayoutOverlay { Layers = EquatableArray.From(layers), Labels = EquatableArray.From(labels) };
    }

    /// <summary>Names a holding threat for its area's label.</summary>
    internal static string ThreatCause(EmergencyThreat t)
    {
        switch (t.Kind)
        {
            case ThreatKind.HuntingPredator:
                return "predator";
            case ThreatKind.HostileBuilding:
                return t.Definition.ToLowerInvariant();
        }
        if (t.Animal.TryGetValue(out var animal) && animal)
        {
            return "manhunter";
        }
        return "raid";
    }

    /// <summary>Labels each 4-connected area of cells once, at its first seed, naming every cause seeded inside it.</summary>
    internal static List<OverlayLabel> AreaLabels(ISet<Cell> cells, IReadOnlyList<Cell> seeds, IReadOnlyDictionary<Cell, string> cause)
    {
        var area = new Dictionary<Cell, int>();
        var seedCells = new List<Cell>();
        var causes = new List<HashSet<string>>();
        foreach (var seed in seeds)
        {
            if (area.TryGetValue(seed, out var done))
            {
                causes[done].Add(cause[seed]);
                continue;
            }
            var id = seedCells.Count;
            area[seed] = id;
            var queue = new Queue<Cell>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                Cell[] around = [new(c.X + 1, c.Z), new(c.X - 1, c.Z), new(c.X, c.Z + 1), new(c.X, c.Z - 1)];
                foreach (var n in around)
                {
                    if (cells.Contains(n) && !area.ContainsKey(n))
                    {
                        area[n] = id;
                        queue.Enqueue(n);
                    }
                }
            }
            seedCells.Add(seed);
            causes.Add([cause[seed]]);
        }
        var labels = new List<OverlayLabel>(seedCells.Count);
        for (var i = 0; i < seedCells.Count; i++)
        {
            var names = causes[i].Order(StringComparer.Ordinal);
            labels.Add(new OverlayLabel { Cell = seedCells[i], Text = string.Join(", ", names) });
        }
        return labels;
    }

    /// <summary>The map's outer margin cells deep as non-overlapping rectangles: bottom and top rows full width, then the two sides.</summary>
    internal static List<Rectangle> EdgeBand(Bounds b, int margin)
    {
        if (b.Width <= 0 || b.Height <= 0 || margin <= 0)
        {
            return [];
        }
        var m = Math.Min(margin, Math.Min(b.Height / 2, b.Width / 2));
        if (m <= 0)
        {
            return [new Rectangle { Width = b.Width, Height = b.Height }];
        }
        var output = new List<Rectangle>
        {
            new() { X = 0, Z = 0, Width = b.Width, Height = m },
            new() { X = 0, Z = b.Height - m, Width = b.Width, Height = m },
        };
        var side = b.Height - (2 * m);
        if (side > 0)
        {
            output.Add(new Rectangle { X = 0, Z = m, Width = m, Height = side });
            output.Add(new Rectangle { X = b.Width - m, Z = m, Width = m, Height = side });
        }
        return output;
    }

    internal static bool InBounds(Cell c, Bounds b) => c.X >= 0 && c.Z >= 0 && c.X < b.Width && c.Z < b.Height;

    /// <summary>The set's cells in row-major order (Go ranged the map; the caller canonicalises).</summary>
    internal static List<Cell> SetCells(IEnumerable<Cell> set) =>
        set.OrderBy(c => c.Z).ThenBy(c => c.X).ToList();
}
