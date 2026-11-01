using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

// The outdoor field comparison (task 6.1 of redefine-habitat-requirements, RG-HABITAT-006): every selected field demand's crops
// are generated as bounded placements over the plan's field blocks (grow a standing zone the crop adopts, or open a new crop
// block), each priced by what it serves from the observed ground: only usable cells earn credit (soil at the crop's floor,
// unroofed and lit, in the crop's growing season, mature before the season ends, compatible pollution, reachable from the field
// anchor over observed walkable ground) and every placement charges its sow, harvest and carry work. The demands compete for one
// ground ledger: a round places the best placement of all the demands still open, its cells leave the ledger, and the rest are
// generated again on what is left, so two crops wanting the same patch resolve by comparison and never share cells. Deferring
// (the current zones unchanged) is the baseline: a placement that serves nothing never beats it. The Need owners bind their own
// winners through the growing-zone orders; nothing here orders work.

/// <summary>
/// One selected field demand: the Need that derived it and the crops that would serve it, each with the normal-soil cells it
/// still needs. Food carries its viable crops (any one serves); hay, a social crop and a resource field carry one crop each.
/// </summary>
public sealed record FieldDemand(NeedId Need, EquatableArray<FieldBlockOption> Options);

/// <summary>
/// A growing-zone order a Need still has in flight (a create of <see cref="Crop"/>, or an add-cell edit of observed zone
/// <see cref="Zone"/>): its cells stay off the ledger until the zone is observed on them, and it serves its own demand meanwhile.
/// </summary>
public sealed record FieldPendingHold(NeedId Need, string Zone, string Crop, EquatableArray<Cell> Cells);

/// <summary>
/// What a placement serves and costs: the share of its demand its cells meet (the native fertility growth factor summed
/// over the usable cells, over the cells the demand needs at normal soil, at most one) and the work a day they take (each cell's
/// sow and harvest work and its walk from the field anchor, once a grow cycle).
/// </summary>
public sealed record FieldPlacementEffect(double Share, double WorkTicksPerDay);

/// <summary>A placement's standing: assessed with its effect, excluded by a named constraint, or not assessed for a named unread fact.</summary>
public abstract record FieldPlacementStanding
{
    private FieldPlacementStanding() { }

    public sealed record Assessed(FieldPlacementEffect Effect) : FieldPlacementStanding;

    /// <summary>A named constraint rules it out: <c>growing season:&lt;crop&gt;</c>, <c>grower skill:&lt;crop&gt;</c> or <c>land:&lt;crop&gt;</c>.</summary>
    public sealed record Excluded(string Reason) : FieldPlacementStanding;

    /// <summary>A fact it needs is unread (rule 9): it is never chosen and names the fact.</summary>
    public sealed record Unassessed(string Fact) : FieldPlacementStanding;
}

/// <summary>
/// One compared placement of <see cref="Crop"/> (the demand's option): <see cref="Edit"/> is the step it binds in plan field
/// block <see cref="Block"/> (FieldBlocks order), null for a crop-level standing that reached no block.
/// </summary>
public sealed record FieldPlacementAlternative(string Crop, int Block, FieldBlockEdit? Edit, FieldPlacementStanding Standing);

/// <summary>
/// One demand's compared placements, as generated on the ground still free in the round that settled it, and the one chosen;
/// <see cref="Chosen"/> is null when none beat deferring, and <see cref="Reason"/> then names why (each option's standing).
/// </summary>
public sealed record FieldPlacementDecision(FieldDemand Demand, EquatableArray<FieldPlacementAlternative> Alternatives)
{
    public FieldPlacementAlternative? Chosen { get; init; }

    /// <summary>True when the demand's own growing-zone order is still in flight: it binds nothing until that order settles.</summary>
    public bool Awaiting { get; init; }

    public string Reason { get; init; } = "";
}

/// <summary>The decisions in demand order; <see cref="Reason"/> names a refusal of the whole comparison (no plan, no blocks, nothing needed).</summary>
public sealed record FieldPlacementChoice(EquatableArray<FieldPlacementDecision> Decisions, string Reason);

/// <summary>
/// What the comparison reads: the facts (layout plan and definition catalog), the observed planning window and zone census
/// (null when unread), the field anchor the colony works from, the selected demands, the ground other sites hold (held
/// building reservations, pending monuments, shells, the firebreak), the crop climate, the best available grower's Plants
/// skill and the catalog's crops (an adopted zone's crop is priced by its own row).
/// </summary>
public sealed record FieldPlacementRequest
{
    public required ReviewFacts Facts { get; init; }

    public IReadOnlyList<SiteCell>? Cells { get; init; }

    public ZonesSnapshot? Zones { get; init; }

    public Cell Anchor { get; init; }

    public IReadOnlyList<FieldDemand> Demands { get; init; } = [];

    public IReadOnlyList<Cell> Protected { get; init; } = [];

    public required CropClimate Climate { get; init; }

    public int GrowerSkill { get; init; }

    public IReadOnlyList<CropChoice> Crops { get; init; } = [];

    /// <summary>The field Needs' growing-zone orders still in flight, taken on the ledger before any round.</summary>
    public IReadOnlyList<FieldPendingHold> Pending { get; init; } = [];
}

public static class FieldPlacementRules
{
    /// <summary>The ticks a baseline colonist spends walking one cell: the game second over the colonist's move speed.</summary>
    internal const double WalkTicksPerCell = ForecastIsolation.TicksPerSecond / CombatManhunterRules.ColonistMoveSpeed;

    /// <summary>
    /// Compares the demands' placements and settles them one round at a time on one ground ledger (see the file comment). The
    /// planning window and zone census are required once any field cell is needed.
    /// </summary>
    public static FieldPlacementChoice Choose(FieldPlacementRequest r)
    {
        if (r.Facts.LayoutPlan is not { } plan)
        {
            return new([], "no layout plan");
        }
        var blocks = plan.FieldBlocks(r.Anchor);
        if (blocks.Count == 0)
        {
            return new([], "layout plan has no field blocks");
        }
        if (!r.Demands.Any(d => d.Options.Any(o => o.Needed > 0)))
        {
            return new([], "no field cells needed");
        }
        var ground = new Ground(r, blocks);
        var decisions = new FieldPlacementDecision?[r.Demands.Count];
        var open = Enumerable.Range(0, r.Demands.Count).Where(i => r.Demands[i].Options.Any(o => o.Needed > 0)).ToList();
        for (var i = 0; i < r.Demands.Count; i++)
        {
            if (!open.Contains(i))
            {
                decisions[i] = new FieldPlacementDecision(r.Demands[i], []) { Reason = "no field cells needed" };
            }
        }
        // An order still in flight holds its ground as a planned site does, and its demand waits on it: binding again would
        // offer the same cells to another crop or open a second zone for the same demand.
        foreach (var hold in r.Pending)
        {
            if (ground.Hold(hold) is not { } crop)
            {
                continue;
            }
            var served = open.FirstOrDefault(i => r.Demands[i].Need == hold.Need && ground.Serves(r.Demands[i], crop), -1);
            if (served >= 0)
            {
                decisions[served] = new FieldPlacementDecision(r.Demands[served], []) { Reason = "pending:" + crop, Awaiting = true };
                open.Remove(served);
            }
        }
        while (open.Count > 0)
        {
            var generated = open.Select(i => (Index: i, Alternatives: ground.Alternatives(r.Demands[i]))).ToList();
            (int Index, FieldPlacementAlternative Alternative)? best = null;
            foreach (var (index, alternatives) in generated)
            {
                foreach (var a in alternatives)
                {
                    if (a.Standing is FieldPlacementStanding.Assessed { Effect.Share: > 0 } && (best is not { } b || Compare(index, a, b.Index, b.Alternative) < 0))
                    {
                        best = (index, a);
                    }
                }
            }
            if (best is not { } winner)
            {
                foreach (var (index, alternatives) in generated)
                {
                    decisions[index] = new FieldPlacementDecision(r.Demands[index], EquatableArray.From(alternatives)) { Reason = ReasonOf(alternatives) };
                }
                break;
            }
            var settled = generated.Single(g => g.Index == winner.Index);
            decisions[winner.Index] = new FieldPlacementDecision(r.Demands[winner.Index], EquatableArray.From(settled.Alternatives)) { Chosen = winner.Alternative };
            ground.Take(winner.Alternative, ground.Grown(r.Demands[winner.Index], winner.Alternative));
            open.Remove(winner.Index);
        }
        return new(EquatableArray.From(decisions.Select(d => d!)), "");
    }

    /// <summary>
    /// Orders two assessed placements, the better first: the larger share of its demand served, then the less work a day per
    /// share served, then a standing zone grown before a new block (one zone keeps its sowers on one field), then the earlier
    /// demand, crop, block and first cell, so equal placements settle the same way every review.
    /// </summary>
    public static int Compare(int ai, FieldPlacementAlternative a, int bi, FieldPlacementAlternative b)
    {
        var x = ((FieldPlacementStanding.Assessed)a.Standing).Effect;
        var y = ((FieldPlacementStanding.Assessed)b.Standing).Effect;
        if (x.Share != y.Share) return y.Share.CompareTo(x.Share);
        var xc = x.WorkTicksPerDay / x.Share;
        var yc = y.WorkTicksPerDay / y.Share;
        if (xc != yc) return xc.CompareTo(yc);
        var xg = a.Edit!.Zone.Length > 0;
        var yg = b.Edit!.Zone.Length > 0;
        if (xg != yg) return yg.CompareTo(xg);
        if (ai != bi) return ai.CompareTo(bi);
        var crop = string.CompareOrdinal(a.Crop, b.Crop);
        if (crop != 0) return crop;
        if (a.Block != b.Block) return a.Block.CompareTo(b.Block);
        var ac = a.Edit.Cells[0];
        var bc = b.Edit.Cells[0];
        return ac.Z != bc.Z ? ac.Z.CompareTo(bc.Z) : ac.X.CompareTo(bc.X);
    }

    /// <summary>Why a demand placed nothing: its options' standings, distinct, in option order.</summary>
    private static string ReasonOf(IReadOnlyList<FieldPlacementAlternative> alternatives)
    {
        var reasons = alternatives.Select(a => a.Standing is FieldPlacementStanding.Excluded e ? e.Reason
            : a.Standing is FieldPlacementStanding.Unassessed u ? u.Fact
            : "share:" + a.Crop).Distinct(StringComparer.Ordinal).ToList();
        return reasons.Count == 0 ? "no field cells needed" : string.Join(",", reasons);
    }

    /// <summary>
    /// The observed ground the round generates on: the window's cells by position, the rich ones, the growing zones and their
    /// crops, the tree and crop zone cells the native sower keeps apart, each cell's walk from the anchor, and the ledger of cells
    /// settled placements took.
    /// </summary>
    private sealed class Ground
    {
        private readonly FieldPlacementRequest _r;
        private readonly IReadOnlyList<HashSet<Cell>> _blocks;
        private readonly Dictionary<Cell, SiteCell> _census = [];
        private readonly HashSet<Cell> _rich = [];
        private readonly HashSet<Cell> _blocked;
        private readonly Dictionary<string, string> _growing = new(StringComparer.Ordinal);
        private readonly HashSet<string> _inedible = new(StringComparer.Ordinal);
        private readonly HashSet<Cell> _treeZone = [];
        private readonly HashSet<Cell> _cropZone = [];
        private readonly Dictionary<Cell, int> _walk;
        private readonly HashSet<Cell> _taken = [];
        private readonly double _richest;

        internal Ground(FieldPlacementRequest r, IReadOnlyList<HashSet<Cell>> blocks)
        {
            _r = r;
            _blocks = blocks;
            var cells = r.Cells ?? throw new RequiredFactException("planning window");
            var zones = r.Zones ?? throw new RequiredFactException("zone census");
            _blocked = [.. r.Protected];
            foreach (var c in cells)
            {
                _census[c.Cell] = c;
                _richest = Math.Max(_richest, c.Fertility);
                if (c.Fertility > LayoutFieldPlanning.FieldRichFertility)
                {
                    _rich.Add(c.Cell);
                }
            }
            foreach (var f in zones.Zones.OfType<ZoneState.Growing>())
            {
                _growing[f.Id] = f.Crop;
            }
            var trees = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in r.Facts.Definitions)
            {
                // The catalog includes buildings and plants with no harvest product; only actual harvests classify a growing
                // crop as edible or as a tree.
                if (d.Harvest is not { } harvest)
                {
                    continue;
                }
                if (!harvest.Edible)
                {
                    _inedible.Add(d.Name);
                }
                if ((d.Plant ?? throw new RequiredFactException("planning Plant")).BlockAdjacentSow && harvest.HarvestDestroysPlant)
                {
                    trees.Add(d.Name);
                }
            }
            // The native sower leaves a crop beside a sown tree and a tree beside a sown crop unsown, so a zone keeps one cell
            // clear of every zone of the other kind.
            foreach (var c in cells)
            {
                if (c.ZoneId is { } id && _growing.TryGetValue(id, out var crop))
                {
                    (trees.Contains(crop) ? _treeZone : _cropZone).Add(c.Cell);
                }
            }
            _walk = Walks(cells, r.Anchor);
        }

        /// <summary>
        /// Each cell's walk in cells from the anchor over observed walkable ground, flooded from the walkable cells nearest the
        /// anchor (the anchor itself may stand in a planned room not yet dug); a cell the flood does not reach is unreachable.
        /// </summary>
        private static Dictionary<Cell, int> Walks(IReadOnlyList<SiteCell> cells, Cell anchor)
        {
            var walkable = new HashSet<Cell>(cells.Where(c => c.Walkable).Select(c => c.Cell));
            var output = new Dictionary<Cell, int>();
            if (walkable.Count == 0)
            {
                return output;
            }
            var near = walkable.Min(c => LayoutFieldPlanning.CellDist(c, anchor));
            var queue = new Queue<Cell>();
            foreach (var c in LayoutFieldPlanning.SortedCells(walkable.Where(c => LayoutFieldPlanning.CellDist(c, anchor) == near)))
            {
                output[c] = (int)Math.Ceiling(Math.Sqrt(near));
                queue.Enqueue(c);
            }
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var n in new[] { new Cell(c.X, c.Z - 1), new Cell(c.X - 1, c.Z), new Cell(c.X + 1, c.Z), new Cell(c.X, c.Z + 1) })
                {
                    if (walkable.Contains(n) && !output.ContainsKey(n))
                    {
                        output[n] = output[c] + 1;
                        queue.Enqueue(n);
                    }
                }
            }
            return output;
        }

        /// <summary>Takes a settled placement's cells off the ledger; they join the zone cells of their kind the native sower keeps apart.</summary>
        internal void Take(FieldPlacementAlternative settled, CropChoice grown)
        {
            var edit = settled.Edit!;
            _taken.UnionWith(edit.Cells);
            (TreePlantationRules.IsTreeCrop(grown) ? _treeZone : _cropZone).UnionWith(edit.Cells);
        }

        /// <summary>
        /// Takes an in-flight order's cells off the ledger, except those the observed census already zones (the fact replaces
        /// the hold); returns the crop it grows while any cell is still unobserved, else null.
        /// </summary>
        internal string? Hold(FieldPendingHold hold)
        {
            var crop = hold.Crop != "" ? hold.Crop : _growing.GetValueOrDefault(hold.Zone, "");
            var live = hold.Cells.Where(c => !(_census.TryGetValue(c, out var cell) && cell.Zone)).ToList();
            if (live.Count == 0)
            {
                return null;
            }
            _taken.UnionWith(live);
            var grown = _r.Crops.FirstOrDefault(c => c.Name == crop)
                ?? _r.Demands.SelectMany(d => d.Options).Select(o => o.Crop).FirstOrDefault(c => c.Name == crop);
            if (grown is not null)
            {
                (TreePlantationRules.IsTreeCrop(grown) ? _treeZone : _cropZone).UnionWith(live);
            }
            return crop;
        }

        /// <summary>Whether a zone of <paramref name="crop"/> serves the demand: one of its crops, or a standing zone its crop adopts.</summary>
        internal bool Serves(FieldDemand demand, string crop) =>
            demand.Options.Any(o => o.Crop.Name == crop || LayoutFieldPlanning.AdoptsZone(o.Crop, crop, _inedible));

        /// <summary>The crop a placement grows: the option's own, or the catalog row of the standing zone's crop it adopts.</summary>
        internal CropChoice Grown(FieldDemand demand, FieldPlacementAlternative a) =>
            demand.Options.Select(o => o.Crop).FirstOrDefault(c => c.Name == a.Edit!.Crop) ?? _r.Crops.First(c => c.Name == a.Edit!.Crop);

        /// <summary>A demand's placements on the ground still free: per option, its crop-level standing or one placement per block it can use.</summary>
        internal List<FieldPlacementAlternative> Alternatives(FieldDemand demand)
        {
            var output = new List<FieldPlacementAlternative>();
            foreach (var option in demand.Options)
            {
                if (option.Needed <= 0)
                {
                    continue;
                }
                if (CropStanding(option.Crop) is { } standing)
                {
                    output.Add(new FieldPlacementAlternative(option.Crop.Name, -1, null, standing));
                    continue;
                }
                var placed = new List<FieldPlacementAlternative>();
                for (var b = 0; b < _blocks.Count; b++)
                {
                    placed.AddRange(InBlock(option, b));
                }
                output.AddRange(placed.Count > 0 ? placed : [new FieldPlacementAlternative(option.Crop.Name, -1, null, new FieldPlacementStanding.Excluded("land:" + option.Crop.Name))]);
            }
            return output;
        }

        /// <summary>
        /// The crop-level standing that rules out every placement of the crop, null when it may be placed: its growth or work
        /// unread, its growing season shut now (the native GrowthSeasonNow: the outdoor temperature inside the crop's growth band,
        /// in a biome whose sky lights the ground), or no available grower meeting its sowing skill.
        /// </summary>
        private FieldPlacementStanding? CropStanding(CropChoice crop)
        {
            if (!FieldCapacityRules.FieldPositive(crop.GrowDays) || !double.IsFinite(crop.FertilitySensitivity))
            {
                return new FieldPlacementStanding.Unassessed("crop growth:" + crop.Name);
            }
            if (!double.IsFinite(crop.SowWork) || crop.SowWork < 0 || !double.IsFinite(crop.HarvestWork) || crop.HarvestWork < 0)
            {
                return new FieldPlacementStanding.Unassessed("crop work:" + crop.Name);
            }
            var outdoor = _r.Climate.Census.OutdoorTemperatureC;
            var remaining = _r.Climate.DaysRemaining;
            if (!double.IsFinite(outdoor) || !double.IsFinite(remaining))
            {
                return new FieldPlacementStanding.Unassessed("growing season:" + crop.Name);
            }
            // A crop a harvest ends must mature before the season's growing days run out, even on the richest observed soil.
            var richest = Factor(crop, _richest);
            if (_r.Climate.OutdoorsDark || outdoor <= crop.MinGrowthTemperature || outdoor >= crop.MaxGrowthTemperature
                || !TreePlantationRules.IsTreeCrop(crop) && richest > 0 && crop.GrowDays / richest > remaining)
            {
                return new FieldPlacementStanding.Excluded("growing season:" + crop.Name);
            }
            if (_r.GrowerSkill < crop.SowMinSkill)
            {
                return new FieldPlacementStanding.Excluded("grower skill:" + crop.Name);
            }
            return null;
        }

        /// <summary>
        /// A cell the crop can use: free observed soil at its floor (FreeFieldSoil), off the held ground and the ledger, clear of
        /// the other kind's zones, pollution-compatible, reachable, and (for a crop a harvest ends) maturing before the season's
        /// remaining growing days end at the cell's fertility. A tree resumes growing next season, so the season bounds no tree.
        /// </summary>
        private bool Usable(Cell cell, CropChoice crop)
        {
            if (!_census.TryGetValue(cell, out var c) || _blocked.Contains(cell) || _taken.Contains(cell) || !LayoutFieldPlanning.FreeFieldSoil(c, crop.FertilityMin)
                || !CropResilienceRules.CropSoilCompatible(crop, c) || !_walk.ContainsKey(cell))
            {
                return false;
            }
            var tree = TreePlantationRules.IsTreeCrop(crop);
            if (NearZone(tree ? _cropZone : _treeZone, cell))
            {
                return false;
            }
            var factor = Factor(crop, c.Fertility);
            return factor > 0 && (tree || crop.GrowDays / factor <= _r.Climate.DaysRemaining);
        }

        private static bool NearZone(HashSet<Cell> zone, Cell cell)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (zone.Contains(new Cell(cell.X + dx, cell.Z + dz)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>The native fertility growth factor (PlantUtility.GrowthRateFactorFor_Fertility) relative to normal soil.</summary>
        private static double Factor(CropChoice crop, double fertility) => SiteRules.CropYieldRate(crop, 1, fertility) / SiteRules.CropYieldRate(crop, 1, 1);

        /// <summary>
        /// The option's placements in one block: growing the largest standing zone the crop adopts by rings (the zone keeps its
        /// crop, priced by that crop's row) and a new connected crop block against the block's standing zones.
        /// </summary>
        private List<FieldPlacementAlternative> InBlock(FieldBlockOption option, int b)
        {
            var block = _blocks[b];
            var zoneCells = new Dictionary<string, HashSet<Cell>>(StringComparer.Ordinal);
            foreach (var cell in block)
            {
                if (_census.TryGetValue(cell, out var c) && c.ZoneId is { Length: > 0 } id && _growing.ContainsKey(id))
                {
                    if (!zoneCells.TryGetValue(id, out var set))
                    {
                        zoneCells[id] = set = [];
                    }
                    set.Add(cell);
                }
            }
            var occupied = new HashSet<Cell>(zoneCells.Values.SelectMany(s => s));
            occupied.UnionWith(block.Where(_taken.Contains));
            var output = new List<FieldPlacementAlternative>();
            var zone = "";
            foreach (var (id, cells) in zoneCells)
            {
                if (LayoutFieldPlanning.AdoptsZone(option.Crop, _growing[id], _inedible)
                    && (zone == "" || cells.Count > zoneCells[zone].Count || cells.Count == zoneCells[zone].Count && string.CompareOrdinal(id, zone) < 0))
                {
                    zone = id;
                }
            }
            if (zone != "")
            {
                var zoneCrop = _growing[zone];
                var grown = zoneCrop == option.Crop.Name ? option.Crop : _r.Crops.FirstOrDefault(c => c.Name == zoneCrop);
                FieldPlacementStanding? standing = grown is null ? new FieldPlacementStanding.Unassessed("crop definition:" + zoneCrop) : CropStanding(grown);
                if (standing is FieldPlacementStanding.Unassessed)
                {
                    output.Add(new FieldPlacementAlternative(option.Crop.Name, b, null, standing));
                }
                else if (grown is not null && standing is null)
                {
                    var adds = LayoutFieldPlanning.ConnectedAdds(zoneCells[zone], Free(block, grown), option.Needed);
                    if (adds.Count > 0)
                    {
                        output.Add(Priced(option, grown, b, new FieldBlockEdit(zone, zoneCrop, adds)));
                    }
                }
            }
            var picked = LayoutFieldPlanning.ConnectedPick(Free(block, option.Crop), occupied, _rich, _r.Anchor, option.Needed);
            if (picked.Count > 0)
            {
                output.Add(Priced(option, option.Crop, b, new FieldBlockEdit("", option.Crop.Name, picked)));
            }
            return output;
        }

        private HashSet<Cell> Free(HashSet<Cell> block, CropChoice crop) => [.. block.Where(cell => Usable(cell, crop))];

        /// <summary>Prices a placement: the share of the option's need its usable cells meet, and their sow, harvest and walk work a day.</summary>
        private FieldPlacementAlternative Priced(FieldBlockOption option, CropChoice grown, int b, FieldBlockEdit edit)
        {
            var credit = 0.0;
            var work = 0.0;
            foreach (var cell in edit.Cells)
            {
                var factor = Factor(grown, _census[cell].Fertility);
                credit += factor;
                work += (grown.SowWork + grown.HarvestWork + (_walk[cell] * WalkTicksPerCell)) * factor / grown.GrowDays;
            }
            var share = Math.Min(1, credit / option.Needed);
            return new FieldPlacementAlternative(option.Crop.Name, b, edit, new FieldPlacementStanding.Assessed(new FieldPlacementEffect(share, work)));
        }
    }
}
