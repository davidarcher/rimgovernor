namespace RimGovernor.Brain.Policy;

/// <summary>One zone's surplus: <see cref="Order"/> is the zone's cells in removal order (outermost from its centre first), <see cref="Zone"/> its cells, and <see cref="Remove"/> how many to give up.</summary>
public sealed record FieldShrink(string Id, string Crop, HashSet<Cell> Zone, IReadOnlyList<Cell> Order, int Remove);

public static class LayoutFieldShrink
{
    // Field shrink hysteresis: a crop's growing zones shrink only once their cells exceed its full target by
    // ShrinkTrigger, and then only down to ShrinkFloor of the target, so a small dip in demand never flips a zone
    // between growing and shrinking.
    public const double ShrinkTrigger = 1.25;
    public const double ShrinkFloor = 1.10;

    /// <summary>
    /// Each crop's full field target in cells: a food crop's FieldTarget for the colony, haygrass for the pens'
    /// whole need, and the fixed social ceiling. A crop whose target is unknown is absent and never shrinks.
    /// </summary>
    public static Dictionary<string, int> FieldTargets(FieldRequest field, (string Crop, int Needed, bool Found) hay, IReadOnlyList<CropChoice> socialCrops)
    {
        var output = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var crop in field.Choices)
        {
            var edible = crop.Edible;
            if (!edible)
            {
                continue;
            }
            output[crop.Name] = CropChoiceRules.FieldTarget(field.Colonists, crop, field.ReserveDays, field.Climate.DaysRemaining);
            // Growth runs to the capacity target, so shrink waits past it.
            var capacity = FieldCapacityRules.FieldCapacityTarget(field.Colonists, crop, field.ReserveDays);
            // Growth runs to the capacity target, so shrink waits past it.
            {
                output[crop.Name] = Math.Max(output.GetValueOrDefault(crop.Name), capacity);
            }
        }
        var (hayName, hayNeeded, hayFound) = hay;
        if (hayFound)
        {
            output[hayName] = hayNeeded;
        }
        foreach (var crop in socialCrops)
        {
            output[crop.Name] = SocialCropsRules.SocialCropCells;
        }
        return output;
    }

    /// <summary>
    /// Finds the first crop (by name) whose standing growing zones exceed its full target in cells past the
    /// hysteresis band, and returns its largest zone. Crops with no positive target never shrink, and a zone
    /// never gives up its last cell. Other zones never move.
    /// </summary>
    public static FieldShrink? PlanFieldShrink(IReadOnlyList<SiteCell>? observedCells, RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot? census, IReadOnlyDictionary<string, int> targets)
    {
        var growing = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in (census ?? throw new RequiredFactException("zone census")).Zones.OfType<RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing>())
        {
            growing[f.Id] = f.Crop;
        }
        var zones = new Dictionary<string, HashSet<Cell>>(StringComparer.Ordinal);
        foreach (var c in observedCells ?? throw new RequiredFactException("planning window"))
        {
            if (c.ZoneId is { Length: > 0 } id && growing.ContainsKey(id))
            {
                if (!zones.TryGetValue(id, out var set))
                {
                    zones[id] = set = [];
                }
                set.Add(c.Cell);
            }
        }
        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        var largest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, cells) in zones)
        {
            var crop = growing[id];
            total[crop] = total.GetValueOrDefault(crop) + cells.Count;
            if (!largest.TryGetValue(crop, out var l) || cells.Count > zones[l].Count || cells.Count == zones[l].Count && string.CompareOrdinal(id, l) < 0)
            {
                largest[crop] = id;
            }
        }
        foreach (var crop in targets.Keys.Order(StringComparer.Ordinal))
        {
            var target = targets[crop];
            if (target <= 0 || total.GetValueOrDefault(crop) <= target * ShrinkTrigger)
            {
                continue;
            }
            var keep = (int)Math.Ceiling(target * ShrinkFloor);
            var id = largest[crop];
            var cells = zones[id];
            var remove = Math.Min(total[crop] - keep, cells.Count - 1);
            if (remove <= 0)
            {
                continue;
            }
            long sx = 0;
            long sz = 0;
            foreach (var c in cells)
            {
                sx += c.X;
                sz += c.Z;
            }
            long n = cells.Count;
            var centre = new Cell((int)(sx / n), (int)(sz / n));
            // OrderByDescending is stable over the row-major order.
            var order = LayoutFieldPlanning.SortedCells(cells).OrderByDescending(c => LayoutFieldPlanning.CellDist(c, centre)).ToList();
            return new FieldShrink(id, crop, cells, order, remove);
        }
        return null;
    }

    /// <summary>The cells of <paramref name="order"/> the mirror holds with no plant standing on them: unsown or harvested soil. A fogged cell is not held, so never bare.</summary>
    public static HashSet<Cell> BareCells(IEnumerable<SiteCell> held, IEnumerable<Cell> order)
    {
        var want = new HashSet<Cell>(order);
        var bare = new HashSet<Cell>();
        foreach (var cell in held)
        {
            if (want.Contains(cell.Cell) && !cell.Things.Any(t => t.Category == ThingCategory.Plant))
            {
                bare.Add(cell.Cell);
            }
        }
        return bare;
    }

    /// <summary>Takes up to <c>s.Remove</c> of the zone's bare (unsown or harvested) cells, outermost first, skipping any whose removal would split what remains: native refuses a disconnected zone.</summary>
    public static List<Cell> ShrinkCells(FieldShrink s, HashSet<Cell> bare)
    {
        var left = new HashSet<Cell>(s.Zone);
        var output = new List<Cell>();
        foreach (var c in s.Order)
        {
            if (output.Count >= s.Remove || left.Count <= 1)
            {
                break;
            }
            if (!bare.Contains(c))
            {
                continue;
            }
            left.Remove(c);
            var start = left.First();
            if (LayoutFieldPlanning.CellComponent(left, start).Count != left.Count)
            {
                left.Add(c);
                continue;
            }
            output.Add(c);
        }
        LayoutFieldPlanning.SortCells(output);
        return output;
    }
}
