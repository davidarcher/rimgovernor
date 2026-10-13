using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>One zone's surplus: <see cref="Order"/> is the zone's cells in removal order (outermost from its centre first), <see cref="Zone"/> its cells, and <see cref="Remove"/> how many to give up.</summary>
internal sealed record FieldShrink(string Id, string Crop, HashSet<Cell> Zone, IReadOnlyList<Cell> Order, int Remove);

internal static class LayoutFieldShrink
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
    public static Dictionary<string, int> FieldTargets(ColonyProjection projection, FieldRequest field)
    {
        var output = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var crop in field.Choices)
        {
            if (!crop.Edible.TryGetValue(out var edible) || !edible)
            {
                continue;
            }
            if (PolicyFunctions.FieldTarget(field.Colonists, crop, field.ReserveDays, field.Climate.DaysRemaining).TryGetValue(out var n))
            {
                output[crop.Name] = n;
            }
            // Growth runs to the capacity target, so shrink waits past it.
            if (PolicyFunctions.FieldCapacityTarget(field.Colonists, crop, field.ReserveDays).TryGetValue(out var capacity))
            {
                output[crop.Name] = Math.Max(output.GetValueOrDefault(crop.Name), capacity);
            }
        }
        var (hayName, hayNeeded, hayFound) = HayTarget(projection);
        if (hayFound)
        {
            output[hayName] = hayNeeded;
        }
        foreach (var crop in SocialCrops(projection))
        {
            output[crop.Name] = PolicyFunctions.SocialCropCells;
        }
        return output;
    }

    /// <summary>
    /// Finds the first crop (by name) whose standing growing zones exceed its full target in cells past the
    /// hysteresis band, and returns its largest zone. Crops with no positive target never shrink, and a zone
    /// never gives up its last cell. Other zones never move.
    /// </summary>
    public static FieldShrink? PlanFieldShrink(ColonyProjection facts, IReadOnlyDictionary<string, int> targets)
    {
        var growing = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in facts.Farms)
        {
            growing[f.Id] = f.Crop;
        }
        var zones = new Dictionary<string, HashSet<Cell>>(StringComparer.Ordinal);
        foreach (var c in facts.Cells)
        {
            if (c.ZoneId.TryGetValue(out var id) && id != "" && growing.ContainsKey(id))
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
            // OrderByDescending is stable, like Go's SliceStable over the row-major order.
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

public sealed partial class RoundsFieldPlanner
{
    /// <summary>Gives up one surplus zone's bare cells with remove-cells, committed directly like a grow. Returns null when nothing was shrunk (no surplus, no bare cell, the method already committed).</summary>
    internal async Task<RoundsFieldResult?> ShrinkAsync(ControlState state, StandardState goal, ColonyProjection projection, RoundsReading read, FieldRequest field, CancellationToken call, CancellationToken epoch)
    {
        if (LayoutFieldShrink.PlanFieldShrink(projection, LayoutFieldShrink.FieldTargets(projection, field)) is not { } plan)
        {
            return null;
        }
        var cells = LayoutFieldShrink.ShrinkCells(plan, LayoutFieldShrink.BareCells(projection.Cells, plan.Order));
        if (cells.Count == 0)
        {
            return null;
        }
        var p = _reviewer.Player;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"shrink/{plan.Id}/[{string.Join(" ", cells.Select(c => $"{{{c.X} {c.Z}}}"))}]")));
        var method = new MethodId("fields-" + Convert.ToHexStringLower(digest.AsSpan(0, 16)));
        try
        {
            await p.Journal.LoadMethodAsync(goal.Standard.Id, goal.Standard.Episode, method, call).ConfigureAwait(false);
            return null;
        }
        catch (NotFoundException)
        {
        }
        var id = PlanIds.MintPlanId();
        var value = new ZoneCellEdit(plan.Id, CellEditMode.Remove, cells);
        var action = DomainAction.NewZoneCellEditAction(new ActionId($"{id.Value}-0"), value);
        var committed = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var now = _reviewer.Clock.Now();
        if (p.State() != state || now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw new ControlException("shrink: stale state or read");
        }
        await p.Journal.CommitMethodReasonAsync(goal.Standard.Id, goal.Revision, method, "", committed, call).ConfigureAwait(false);
        ClockEvents.Event("layout", "fields", "field block shrunk", ("zone", plan.Id), ("crop", plan.Crop), ("cells", cells.Count), ("plan", id.Value));
        return new RoundsFieldResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
