namespace RimGovernor.Brain.Policy;

/// <summary>What one Round's export declaration reads. (Go <c>ExportRequest</c>.)</summary>
public sealed record ExportRequest
{
    public IReadOnlyList<GearBench> Benches { get; init; } = [];

    public Fact<EquatableArray<PawnProfile>> Profiles { get; init; }

    public ItemFacts Items { get; init; } = new();

    /// <summary>The silver gap (<c>SilverGap</c>).</summary>
    public Fact<double> Gap { get; init; }

    /// <summary>Gives the floors and needs that put a product out of reach (<c>DerivedDemand.Needs</c>).</summary>
    public DerivedDemand Demand { get; init; } = new();

    /// <summary>The economic trade floors.</summary>
    public IReadOnlyDictionary<string, long> Floors { get; init; } = new Dictionary<string, long>();

    /// <summary>Give the ingredient stock a bill may spend (<c>UsableIngredients</c>) and the 15-day line the guard holds.</summary>
    public IReadOnlyList<ResourceRunway> Runways { get; init; } = [];

    /// <summary>Give the ingredient stock a bill may spend (<c>UsableIngredients</c>) and the 15-day line the guard holds.</summary>
    public IReadOnlyList<Stock> Supply { get; init; } = [];

    /// <summary>The colony stock by definition.</summary>
    public IReadOnlyDictionary<Resource, long> Stock { get; init; } = new Dictionary<Resource, long>();

    /// <summary>The gear above demand (ApparelSurplus, SpareWeaponCounts).</summary>
    public GearSurplus Surplus { get; init; } = new();

    public bool SurplusKnown { get; init; }

    /// <summary>The packed sculptures held: the goods already in flight.</summary>
    public Fact<long> Packed { get; init; }

    /// <summary>
    /// The priced ways to acquire an ingredient by hand (mine, chop, harvest, hunt), per ingredient; none leaves
    /// the produce prior.
    /// </summary>
    public IReadOnlyDictionary<Resource, IReadOnlyList<SupplyCandidate>> Sources { get; init; } =
        new Dictionary<Resource, IReadOnlyList<SupplyCandidate>>();

    /// <summary>Says per product whether a reachable trader buys it.</summary>
    public IReadOnlyDictionary<Resource, Fact<bool>> Buys { get; init; } = new Dictionary<Resource, Fact<bool>>();

    /// <summary>The low end of the silver the best reachable trader carries.</summary>
    public Fact<long> CashCap { get; init; }

    /// <summary>
    /// The work of one piece of a product made from a stuff, for the recipes that name none (the product's
    /// WorkToMake); null reads none. Returns false when the work is not read.
    /// </summary>
    public Func<Resource, Resource, (double Work, bool Ok)>? WorkFor { get; init; }
}

/// <summary>A Round's export declaration with the ranking behind it. (Go <c>ExportPlan</c>.)</summary>
public sealed record ExportPlan
{
    public Declared Declared { get; init; } = new();

    /// <summary>
    /// The products MaintainTrade can rank (every craftable sale good its benches make, floors and the non-sale
    /// kinds left out), the goods a held stack joins the sale surplus of while the gap is open.
    /// </summary>
    public IReadOnlyList<Resource> Products { get; init; } = [];

    /// <summary>The candidates by descending score, for the reader.</summary>
    public IReadOnlyList<ExportCandidate> Ranked { get; init; } = [];

    /// <summary>
    /// The sale value of the goods already held, packed or on standing bills that was netted off the gap.
    /// </summary>
    public double InFlight { get; init; }

    /// <summary>The best <see cref="PolicyFunctions.ExportViewCandidates"/> scored pairs, ordered or not, for the reader.</summary>
    public IReadOnlyList<ExportCandidate> Candidates { get; init; } = [];

    /// <summary>Counts the (recipe, variant, worker) candidates that never reached an order, by why.</summary>
    public IReadOnlyDictionary<ExportDrop, int> Dropped { get; init; } = new Dictionary<ExportDrop, int>();
}

/// <summary>Why a candidate sale good was dropped before it was ordered. (Go <c>ExportDrop</c>.)</summary>
public enum ExportDrop
{
    /// <summary>No reachable trader buys the product.</summary>
    NoBuyer,

    /// <summary>An ingredient the colony may not spend, or lacks.</summary>
    NoIngredient,

    /// <summary>The sale nets at or below zero (or the good is unpriced).</summary>
    NoMargin,

    /// <summary>The runway guard grants no unit of the ingredients.</summary>
    [WireName("runway_guard")]
    Runway,
}

/// <summary>
/// One ranked sale good: a recipe at a bench kind made from one stuff or filter by a worker, with the silver it
/// nets per work tick. Value is the expected market value of one piece, Net its sale value beyond what its
/// ingredients fetch raw, Ticks the crafting ticks plus the ingredients' acquisition ticks and Score = Net /
/// Ticks. Draw is one piece's ingredient units. (Go <c>ExportCandidate</c>.)
/// </summary>
public sealed record ExportCandidate
{
    public string Recipe { get; init; } = "";

    public string BenchKind { get; init; } = "";

    public Resource Product { get; init; }

    public Resource Stuff { get; init; }

    public PawnId Worker { get; init; }

    public double Value { get; init; }

    public double Net { get; init; }

    public double Ticks { get; init; }

    public double Score { get; init; }

    public IReadOnlyList<string> Filter { get; init; } = [];

    public IReadOnlyList<Amount> Draw { get; init; } = [];
}

/// <summary>
/// Decides how much of a draw on the colony's stock an export may take. Admit grants up to want pieces of unit,
/// counting every draw it has granted before (the orders declared this Round, placed or not) and the standing
/// bills'. It is replaceable by a finite-versus-renewable classification of the resource without changing a
/// caller. (Go <c>ExportRunwayGuard</c>.)
/// </summary>
public interface IExportRunwayGuard
{
    (long Granted, bool Known) Admit(IReadOnlyList<Amount> unit, long want);
}

/// <summary>
/// The level-1 guard: every resource is treated as finite, so an export keeps the reserve and
/// <see cref="PolicyFunctions.ExportRunwayHorizonDays"/> of the colony's own observed use. (Go <c>horizonGuard</c>.)
/// </summary>
internal sealed class HorizonGuard : IExportRunwayGuard
{
    private readonly IReadOnlyList<ResourceRunway> _runways;
    private readonly Dictionary<Resource, long> _have = [];
    private readonly Dictionary<Resource, long> _committed = [];

    public HorizonGuard(IReadOnlyList<ResourceRunway> runways, IReadOnlyList<Stock> supply, IReadOnlyDictionary<Resource, long> draws)
    {
        _runways = runways;
        foreach (var s in supply)
        {
            if (s.Available.TryGetValue(out var have) && have >= 0)
            {
                _have[s.Resource] = have;
            }
        }
        foreach (var (r, n) in draws)
        {
            _committed[r] = _committed.GetValueOrDefault(r) + n;
        }
    }

    /// <summary>Known is false while an ingredient's census or the use rate of one its runway forecasts is unread.</summary>
    public (long Granted, bool Known) Admit(IReadOnlyList<Amount> unit, long want)
    {
        var granted = want;
        foreach (var a in unit)
        {
            if (a.Count <= 0)
            {
                continue;
            }
            if (!_have.TryGetValue(a.Resource, out var have))
            {
                return (0, false);
            }
            long line = 0;
            foreach (var row in _runways)
            {
                if (row.Resource != a.Resource)
                {
                    continue;
                }
                if (row.Stock.TryGetValue(out var stock) && stock >= 0)
                {
                    have = Math.Min(have, stock);
                }
                if (!row.ConsumptionPerDay.TryGetValue(out var rate) || !PolicyFunctions.Finite(rate) || row.Reserve < 0)
                {
                    return (0, false);
                }
                line = row.Reserve + (long)Math.Ceiling(Math.Max(0, rate) * PolicyFunctions.ExportRunwayHorizonDays);
            }
            var free = have - line - _committed.GetValueOrDefault(a.Resource);
            granted = Math.Min(granted, Math.Max(0, free) / a.Count);
        }
        if (granted <= 0)
        {
            return (0, true);
        }
        foreach (var a in unit)
        {
            _committed[a.Resource] = _committed.GetValueOrDefault(a.Resource) + (a.Count * granted);
        }
        return (granted, true);
    }
}

/// <summary>A recipe of a usable bench that MaintainTrade may rank. (Go <c>exportRecipe</c>.)</summary>
internal sealed record ExportRecipe(
    GearBench Bench,
    GearRecipe Recipe,
    Resource Product,
    EquatableArray<EquatableArray<Amount>> Slots,
    double Work,
    IReadOnlyList<WorkRequirement> Required,
    bool Quality,
    bool Sculpture
);

/// <summary>The ingredient choices of a recipe: one per stuff a stuff-made product accepts, else the single cheapest filter.</summary>
internal sealed record ExportVariant(Resource Stuff, IReadOnlyList<Resource> Filter, IReadOnlyList<Amount> Picks);

/// <summary>A pawn able to do a recipe's work, with the skill that sets the quality it makes.</summary>
internal readonly record struct ExportWorker(PawnId Id, int Skill);

public static partial class PolicyFunctions
{
    /// <summary>Go <c>MaintainTrade</c>.</summary>
    // MaintainTrade closes the colony's silver gap with goods the bench crafts: while SilverGap is positive it
    // ranks the craftable sale goods and declares the winners to the work ledger (OrderDeclarer) as finite
    // batches pinned to the best capable worker; the trade planner then sells what they make as surplus
    // (ExportSaleSurplus, SaleGearSurplus). Ownership is split by floor: MaintainResource owns a bill whose
    // product has a floor; MaintainTrade owns the sale goods with none, and never acquires anything: it spends
    // only UsableIngredients. MaintainArt keeps room decoration only.
    public static readonly ConcernId MaintainTrade = new("MaintainTrade");

    /// <summary>Ranks MaintainTrade with the other upkeep projects.</summary>
    internal const int TradePriority = 3;

    /// <summary>
    /// How far ahead ExportGuard keeps the colony's own use of an ingredient whole: an export may not draw a
    /// resource below its reserve plus this many days of its observed use.
    /// </summary>
    public const double ExportRunwayHorizonDays = 15.0;

    /// <summary>
    /// The planning prior for what a trader pays against the item's market value (the game's base sell factor,
    /// before negotiation and the trader's own margin). The sale itself is priced from the live sheet.
    /// </summary>
    public const double ExportSellRatio = 0.5;

    /// <summary>Go <c>AllExportDrops</c>: every ExportDrop, in the order the view lists them.</summary>
    public static readonly IReadOnlyList<ExportDrop> AllExportDrops = [ExportDrop.NoBuyer, ExportDrop.NoIngredient, ExportDrop.NoMargin, ExportDrop.Runway];

    /// <summary>Bounds the scored candidates an ExportPlan keeps for the ledger view.</summary>
    public const int ExportViewCandidates = 10;

    private static void AddDrop(Dictionary<ExportDrop, int> drops, ExportDrop why, int n)
    {
        if (n <= 0)
        {
            return;
        }
        drops[why] = drops.GetValueOrDefault(why) + n;
    }

    /// <summary>
    /// Go <c>NewExportGuard</c>: the level-1 guard over the usable census, holding the
    /// <see cref="ExportRunwayHorizonDays"/> line of every ingredient the runways forecast; draws holds the units
    /// already committed to standing and declared bills.
    /// </summary>
    public static IExportRunwayGuard NewExportGuard(IReadOnlyList<ResourceRunway> runways, IReadOnlyList<Stock> supply, IReadOnlyDictionary<Resource, long> draws) =>
        new HorizonGuard(runways, supply, draws);

    /// <summary>
    /// The planning prior for the quality a worker of a skill level turns out, as an index into the quality
    /// scale (Poor at a beginner, Normal at skill 8, Good at 16): the game's quality roll centres near it.
    /// Judgment, not a game number.
    /// </summary>
    private static double ExportQualityCentre(int skill) => Math.Min(4, 1 + (Math.Max(0, skill) / 8.0));

    /// <summary>The MarketValue factor of the expected quality: the quality price factors interpolated at the skill's centre.</summary>
    private static double ExportQualityFactor(int skill)
    {
        var c = ExportQualityCentre(skill);
        var lo = (int)Math.Floor(c);
        var hi = Math.Min(lo + 1, MarketQualityFactor.Length - 1);
        var frac = c - lo;
        return (MarketQualityFactor[lo] * (1 - frac)) + (MarketQualityFactor[hi] * frac);
    }

    /// <summary>
    /// Reports whether a recipe's product is one MaintainTrade may rank. A product with a floor or a need, food,
    /// stone blocks, components and the currency are others' (MaintainResource's), never MaintainTrade's.
    /// </summary>
    private static bool ExportProduct(ExportRequest r, Resource product)
    {
        var i = r.Items;
        if (product.IsEmpty || product == i.Currency || product == ComponentResource || i.IsStoneBlocks(product))
        {
            return false;
        }
        if (r.Demand.Needs.GetValueOrDefault(product) > 0 || r.Floors.GetValueOrDefault(product.Value) > 0 || i.Nutrition.GetValueOrDefault(product) > 0)
        {
            return false;
        }
        return true;
    }

    /// <summary>Reports a product the surplus gear sale sells (apparel and weapons); gear and packed art do not sell from the generic stock surplus.</summary>
    private static bool ExportGear(ItemFacts i, Resource product)
    {
        var categories = i.Categories.GetValueOrDefault(product) ?? [];
        return categories.Contains("Apparel") || categories.Contains("Weapons");
    }

    private static bool ExportQualityBearing(ItemFacts i, GearRecipe recipe, Resource product) => recipe.Role == RecipeRole.Sculpture || ExportGear(i, product);

    /// <summary>
    /// Go <c>ExportSaleSurplus</c>: adds the held stock of the export products to the trade need's surplus,
    /// retaining none of it, while the colony is short of silver (the OrganSaleSurplus rule). Gear and art sell
    /// by their own paths, so products holds only the generic goods.
    /// </summary>
    public static Fact<TradeNeed> ExportSaleSurplus(
        ItemFacts items,
        Fact<TradeNeed> need,
        Fact<EquatableArray<Amount>> resources,
        Fact<long> colonists,
        IReadOnlyList<Resource> products
    )
    {
        var nk = need.TryGetValue(out var n);
        var rk = resources.TryGetValue(out var rows);
        if (!nk || !rk || products.Count == 0 || !Positive(SilverShort(items, need, SilverStock(items, resources), colonists)))
        {
            return need;
        }
        var stock = new Dictionary<Resource, long>();
        foreach (var row in rows!)
        {
            stock[row.Resource] = stock.GetValueOrDefault(row.Resource) + row.Count;
        }
        var ordered = products.OrderBy(p => p.Value, StringComparer.Ordinal).Distinct();
        foreach (var product in ordered)
        {
            var count = stock.GetValueOrDefault(product);
            if (count > 0 && !n!.Surplus.Any(a => a.Resource == product))
            {
                n = n with { Surplus = [.. n.Surplus, new Amount { Resource = product, Count = count }] };
                n = n.Retain(product, 0);
            }
        }
        return Known(n!);
    }

    /// <summary>
    /// Go <c>ExportSaleProducts</c>: the products of an ExportPlan the generic stock surplus sells: its products
    /// less gear (SaleGearSurplus) and sculptures (the packed art sale).
    /// </summary>
    public static IReadOnlyList<Resource> ExportSaleProducts(ItemFacts items, IReadOnlyList<Resource> products) =>
        [.. products.Where(p => !ExportGear(items, p) && !IsSculptureProduct(items, p))];

    private static bool IsSculptureProduct(ItemFacts i, Resource product) => i.Sculptures.Any(s => new Resource(s.Def) == product);

    /// <summary>
    /// The rankable recipes on the usable benches. Unread is set when a recipe that would otherwise be ranked
    /// has an unread fact.
    /// </summary>
    private static (List<ExportRecipe> Recipes, bool Unread) ExportRecipes(ExportRequest r)
    {
        var result = new List<ExportRecipe>();
        var unread = false;
        foreach (var bench in r.Benches)
        {
            var uk = bench.Usable.TryGetValue(out var usable);
            var rk = bench.Recipes.TryGetValue(out var recipes);
            if (!uk || !rk)
            {
                unread = true;
                continue;
            }
            if (!usable)
            {
                continue;
            }
            foreach (var recipe in recipes!)
            {
                if (recipe.Kind != LedgerBillKind.Production || recipe.Products.Count != 1 || !ExportProduct(r, recipe.Products[0]))
                {
                    continue;
                }
                var ak = recipe.Available.TryGetValue(out var available);
                var ok = recipe.AvailableOn.TryGetValue(out var on);
                if (!ak || !ok)
                {
                    unread = true;
                    continue;
                }
                if (!available || !on)
                {
                    continue;
                }
                var sk = recipe.Ingredients.TryGetValue(out var slots);
                // A recipe whose work is the product's WorkToMake carries none here (workOf prices it per
                // stuff); an unpriced one is simply not ranked.
                var work = recipe.WorkAmount.ValueOrDefault;
                var qk = recipe.RequiredWork.TryGetValue(out var required);
                if (!sk || !qk || !Finite(work))
                {
                    unread = true;
                    continue;
                }
                var product = recipe.Products[0];
                result.Add(
                    new ExportRecipe(
                        bench,
                        recipe,
                        product,
                        slots!,
                        work,
                        required!,
                        ExportQualityBearing(r.Items, recipe, product),
                        recipe.Role == RecipeRole.Sculpture
                    )
                );
            }
        }
        return (result, unread);
    }

    /// <summary>The ingredient units of one piece under a filter: per slot the cheapest member the filter admits (the loadout stuff first).</summary>
    private static List<Amount>? ExportPicks(EquatableArray<EquatableArray<Amount>> slots, IReadOnlyList<Resource> filter, Resource stuff)
    {
        var picks = new List<Amount>();
        foreach (var slot in slots)
        {
            var best = new Amount(default, 0);
            foreach (var a in slot)
            {
                if (!filter.Contains(a.Resource) || a.Count <= 0)
                {
                    continue;
                }
                if (best.Resource.IsEmpty)
                {
                    best = a;
                }
                else if (!stuff.IsEmpty && a.Resource == stuff)
                {
                    best = a;
                }
                else if (!stuff.IsEmpty && best.Resource == stuff)
                {
                    // the loadout stuff already holds the slot
                }
                else if (a.Count < best.Count || (a.Count == best.Count && a.Resource < best.Resource))
                {
                    best = a;
                }
            }
            if (best.Resource.IsEmpty)
            {
                return null;
            }
            picks.Add(best);
        }
        return picks;
    }

    private static List<ExportVariant> ExportVariants(ItemFacts i, ExportRecipe e)
    {
        IReadOnlyList<Resource> stuffs = [default];
        if ((i.AcceptedStuff.GetValueOrDefault(e.Product)?.Count ?? 0) > 0)
        {
            stuffs = i.StuffsFor(e.Product);
        }
        var result = new List<ExportVariant>();
        foreach (var stuff in stuffs)
        {
            var filter = GearFilter(e.Slots, stuff, i.StuffCategories);
            if (filter is null)
            {
                continue;
            }
            var picks = ExportPicks(e.Slots, filter, stuff);
            if (picks is null)
            {
                continue;
            }
            result.Add(new ExportVariant(stuff, filter, picks));
        }
        return result;
    }

    /// <summary>
    /// Go <c>AcquisitionTicksPerUnit</c>: the pawn ticks one unit of a resource costs by the cheapest of its hand
    /// sources: the source's labor plus the haul (distance by the trip and its return, plus a tick a trip) over
    /// what it yields. Null when no source prices it.
    /// </summary>
    public static double? AcquisitionTicksPerUnit(IReadOnlyList<SupplyCandidate>? sources)
    {
        var best = 0.0;
        var found = false;
        foreach (var s in sources ?? [])
        {
            var lk = s.UpfrontCost.LaborTicks.TryGetValue(out var labor);
            var pk = s.PathDistance.TryGetValue(out var path);
            if (s.Yields.Count == 0 || !lk || !pk)
            {
                continue;
            }
            if (!s.Yields[0].StockCap.TryGetValue(out var units) || units <= 0)
            {
                continue;
            }
            var trips = 1.0;
            if (s.NeedsHaul && s.UnitsPerTrip > 0)
            {
                trips = Math.Ceiling((double)units / s.UnitsPerTrip);
            }
            var cost = (labor + (path * (1 + (2 * trips))) + trips) / units;
            if (!found || cost < best)
            {
                best = cost;
                found = true;
            }
        }
        return found ? best : null;
    }

    private static double ExportAcquisition(ExportRequest r, Resource resource) =>
        AcquisitionTicksPerUnit(r.Sources.GetValueOrDefault(resource)) ?? AcquisitionLaborPerUnit[CandidateKind.Produce];

    private static List<ExportWorker> ExportWorkers(IReadOnlyList<PawnProfile> profiles, IReadOnlyList<WorkRequirement> required)
    {
        var result = new List<ExportWorker>();
        foreach (var p in profiles)
        {
            if (p.Child || !FoodId(p.Id.Value))
            {
                continue;
            }
            var capable = true;
            var skill = 0;
            var skilled = false;
            foreach (var q in required)
            {
                if (!p.Capable(q.Work, q.Minimum))
                {
                    capable = false;
                    break;
                }
                if (q.Skill.Length != 0 && !skilled)
                {
                    skill = p.Skill(q.Skill).Level;
                    skilled = true;
                }
            }
            if (capable)
            {
                result.Add(new ExportWorker(p.Id, skill));
            }
        }
        return result;
    }

    /// <summary>
    /// Prices one piece of a variant made by a worker of the given skill. Null when the good does not beat
    /// selling its ingredients raw or a price is unread.
    /// </summary>
    private static ExportCandidate? ExportScore(ExportRequest r, ExportRecipe e, ExportVariant v, int skill)
    {
        var items = r.Items;
        var work = ExportWorkOf(r, e, v.Stuff);
        if (work is null)
        {
            return null;
        }
        var raw = 0.0;
        var ticks = work.Value / BenchSpeed(e.Bench);
        foreach (var a in v.Picks)
        {
            if (!items.Market.TryGetValue(a.Resource, out var price))
            {
                return null;
            }
            raw += a.Count * price;
            ticks += a.Count * ExportAcquisition(r, a.Resource);
        }
        double value;
        if (!v.Stuff.IsEmpty)
        {
            value = raw + (work.Value * ValuePerWorkTick);
        }
        else if (items.Market.TryGetValue(e.Product, out var basePrice))
        {
            value = basePrice;
        }
        else
        {
            return null;
        }
        if (e.Quality)
        {
            value *= ExportQualityFactor(skill);
        }
        var net = ExportSellRatio * (value - raw);
        if (!(net > 0) || !(ticks > 0))
        {
            return null;
        }
        return new ExportCandidate
        {
            Recipe = e.Recipe.Definition,
            BenchKind = e.Bench.Def,
            Product = e.Product,
            Stuff = v.Stuff,
            Value = value,
            Net = net,
            Ticks = ticks,
            Score = net / ticks,
            Filter = [.. v.Filter.Select(f => f.Value)],
            Draw = v.Picks,
        };
    }

    /// <summary>
    /// The work one piece takes: the recipe's own amount, else the sculpture's catalog work, else the product's
    /// WorkToMake under the stuff (<see cref="ExportRequest.WorkFor"/>). Null when none is read.
    /// </summary>
    private static double? ExportWorkOf(ExportRequest r, ExportRecipe e, Resource stuff)
    {
        if (e.Work > 0)
        {
            return e.Work;
        }
        foreach (var s in r.Items.Sculptures)
        {
            if (new Resource(s.Def) == e.Product && s.Work > 0 && Finite(s.Work))
            {
                return s.Work;
            }
        }
        if (r.WorkFor is not null)
        {
            var (work, ok) = r.WorkFor(e.Product, stuff);
            if (ok && work > 0 && Finite(work))
            {
                return work;
            }
        }
        return null;
    }

    private static double BenchSpeed(GearBench b) => b.WorkSpeed.TryGetValue(out var speed) && speed > 0 && Finite(speed) ? speed : 1;

    /// <summary>
    /// The active pinned finite batches on the benches that make an export product: placed and unfinished goods
    /// that stay declared as they stand and count against the gap. Unread is set when one's spec is.
    /// </summary>
    private static (List<OrderSpec> Orders, List<GearBill> Bills, bool Unread) StandingExports(IReadOnlyList<GearBench> benches, HashSet<Resource> products)
    {
        var orders = new List<OrderSpec>();
        var bills = new List<GearBill>();
        var unread = false;
        foreach (var b in benches)
        {
            _ = b.Bills.TryGetValue(out var list);
            foreach (var bill in list)
            {
                if (bill.Spent || bill.Products.Count != 1 || !products.Contains(bill.Products[0]))
                {
                    continue;
                }
                if (!bill.Worker.TryGetValue(out var worker) || worker.Length == 0)
                {
                    continue;
                }
                if (bill.Active.TryGetValue(out var active) && !active)
                {
                    continue;
                }
                if (!bill.Spec.TryGetValue(out var spec))
                {
                    unread = true;
                    continue;
                }
                if (spec.Mode != BillMode.GearBatch)
                {
                    continue;
                }
                orders.Add(spec);
                bills.Add(bill);
            }
        }
        return (orders, bills, unread);
    }

    /// <summary>
    /// Go <c>DeclareExportOrders</c>: MaintainTrade's wanted orders. While the silver gap is open every standing
    /// export batch is declared as it stands and, for what the goods held and in progress leave of the gap, one
    /// finite batch per free worker is added in descending score order, sized by the gap, the trader cash cap and
    /// the runway guard. A gap known closed declares nothing, so the ledger removes the batches. Abstain while
    /// the gap, the workers, the buyers or any recipe or standing bill that matters is unread.
    /// </summary>
    public static ExportPlan DeclareExportOrders(ExportRequest r)
    {
        var declared = new Declared();
        var (recipes, unread) = ExportRecipes(r);
        var productSet = new HashSet<Resource>();
        var products = new List<Resource>();
        foreach (var e in recipes)
        {
            if (productSet.Add(e.Product))
            {
                products.Add(e.Product);
            }
        }
        products.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        var drops = new Dictionary<ExportDrop, int>();
        ExportPlan Plan(double inFlight = 0, IReadOnlyList<ExportCandidate>? ranked = null, IReadOnlyList<ExportCandidate>? candidates = null) =>
            new()
            {
                Declared = declared,
                Products = products,
                Ranked = ranked ?? [],
                InFlight = inFlight,
                Candidates = candidates ?? [],
                Dropped = drops,
            };
        if (!r.Gap.TryGetValue(out var gap))
        {
            declared.Unread(Cause.UnreadSilverGap);
            return Plan();
        }
        if (!(gap > 0))
        {
            if (unread)
            {
                declared.Unread(Cause.UnreadRecipes);
            }
            return Plan();
        }
        var pk = r.Profiles.TryGetValue(out var profiles);
        var ck = r.CashCap.TryGetValue(out var cash);
        if (!pk)
        {
            declared.Unread(Cause.UnreadWorkers);
        }
        if (!ck)
        {
            declared.Unread(Cause.UnreadTraderCash);
        }
        if (!pk || !ck)
        {
            return Plan();
        }
        var (standing, bills, standingUnread) = StandingExports(r.Benches, productSet);
        declared.Orders.AddRange(standing);
        if (unread)
        {
            declared.Unread(Cause.UnreadRecipes);
        }
        if (standingUnread)
        {
            declared.Unread(Cause.UnreadBills);
        }
        var busy = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in standing)
        {
            busy.Add(o.Worker);
        }
        // The goods already in flight: held, packed, and on standing bills.
        var inFlight = ExportInFlight(r, recipes, bills);
        gap -= inFlight;
        if (!(gap > 0) || cash <= 0)
        {
            return Plan(inFlight);
        }
        var draws = new Dictionary<Resource, long>();
        foreach (var bill in bills)
        {
            var spec = bill.Spec.ValueOrDefault!;
            foreach (var a in ExportBillDraw(recipes, spec))
            {
                draws[a.Resource] = draws.GetValueOrDefault(a.Resource) + a.Count;
            }
        }
        var guard = NewExportGuard(r.Runways, r.Supply, draws);
        var pairs = new List<ExportCandidate>();
        var usable = new Dictionary<Resource, long>();
        foreach (var q in UsableIngredients(r.Runways, r.Supply))
        {
            usable[q.Key.Def] = q.Count;
        }
        foreach (var e in recipes)
        {
            var buysFact = r.Buys.GetValueOrDefault(e.Product);
            if (!buysFact.TryGetValue(out var buys))
            {
                declared.Unread(Cause.UnreadBuyers);
                continue;
            }
            var variants = ExportVariants(r.Items, e);
            var workers = ExportWorkers(profiles!, e.Required);
            if (!buys)
            {
                AddDrop(drops, ExportDrop.NoBuyer, variants.Count * workers.Count);
                continue;
            }
            // Gear held above demand is unread: the gap cannot be netted, so the good is not ordered.
            if (ExportGear(r.Items, e.Product) && !r.SurplusKnown)
            {
                declared.Unread(Cause.UnreadSurplus);
                continue;
            }
            foreach (var v in variants)
            {
                // An ingredient the colony may not spend drops the candidate.
                if (v.Picks.Any(a => !usable.TryGetValue(a.Resource, out var have) || have < a.Count))
                {
                    AddDrop(drops, ExportDrop.NoIngredient, workers.Count);
                    continue;
                }
                foreach (var w in workers)
                {
                    var c = ExportScore(r, e, v, w.Skill);
                    if (c is not null)
                    {
                        pairs.Add(c with { Worker = w.Id });
                    }
                    else
                    {
                        AddDrop(drops, ExportDrop.NoMargin, 1);
                    }
                }
            }
        }
        pairs = [.. pairs
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Recipe, StringComparer.Ordinal)
            .ThenBy(p => p.Stuff.Value, StringComparer.Ordinal)
            .ThenBy(p => p.Worker.Value, StringComparer.Ordinal)];
        var shown = pairs.Take(ExportViewCandidates).ToList();
        var budget = (double)cash;
        var ranked = new List<ExportCandidate>();
        foreach (var c in pairs)
        {
            if (!(gap > 0) || !(budget > 0))
            {
                break;
            }
            if (busy.Contains(c.Worker.Value))
            {
                continue;
            }
            var price = ExportSellRatio * c.Value;
            if (!(price > 0))
            {
                continue;
            }
            var want = (long)Math.Min(Math.Min(Math.Ceiling(gap / price), Math.Floor(budget / price)), int.MaxValue);
            if (want < 1)
            {
                continue;
            }
            var (granted, known) = guard.Admit(c.Draw, want);
            if (!known)
            {
                declared.Unread(Cause.UnreadRunways);
                continue;
            }
            if (granted < 1)
            {
                AddDrop(drops, ExportDrop.Runway, 1);
                continue;
            }
            busy.Add(c.Worker.Value);
            gap -= granted * price;
            budget -= granted * price;
            ranked.Add(c);
            declared.Orders.Add(
                new OrderSpec
                {
                    Recipe = c.Recipe,
                    Ingredients = EquatableArray.From(c.Filter),
                    Worker = c.Worker.Value,
                    Mode = BillMode.GearBatch,
                    Target = (int)granted,
                    BenchKind = c.BenchKind,
                    Product = c.Product,
                    Class = ResourceClass.Material,
                }
            );
        }
        return Plan(inFlight, ranked, shown);
    }

    /// <summary>
    /// Go <c>ExportIngredients</c>: the ingredients the ranked goods' cheapest picks draw on (every variant of
    /// every rankable recipe): the stock and the hand sources the declaring planner reads to price them. Sorted.
    /// </summary>
    public static IReadOnlyList<Resource> ExportIngredients(ExportRequest r)
    {
        var (recipes, _) = ExportRecipes(r);
        var seen = new HashSet<Resource>();
        foreach (var e in recipes)
        {
            foreach (var v in ExportVariants(r.Items, e))
            {
                foreach (var a in v.Picks)
                {
                    seen.Add(a.Resource);
                }
            }
        }
        return [.. seen.OrderBy(x => x.Value, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Go <c>RankExports</c>: the candidates MaintainTrade would order against an open gap, best first, for the
    /// reader: <see cref="DeclareExportOrders"/>' ranking without the gap's sizing.
    /// </summary>
    public static IReadOnlyList<ExportCandidate> RankExports(ExportRequest r)
    {
        var (recipes, _) = ExportRecipes(r);
        _ = r.Profiles.TryGetValue(out var profiles);
        var result = new List<ExportCandidate>();
        foreach (var e in recipes)
        {
            if (!r.Buys.GetValueOrDefault(e.Product).TryGetValue(out var buys) || !buys)
            {
                continue;
            }
            foreach (var v in ExportVariants(r.Items, e))
            {
                var best = new ExportCandidate();
                foreach (var w in ExportWorkers(profiles, e.Required))
                {
                    var c = ExportScore(r, e, v, w.Skill);
                    if (c is not null && c.Score > best.Score)
                    {
                        best = c with { Worker = w.Id };
                    }
                }
                if (best.Recipe.Length != 0)
                {
                    result.Add(best);
                }
            }
        }
        return [.. result.OrderByDescending(c => c.Score).ThenBy(c => c.Recipe, StringComparer.Ordinal).ThenBy(c => c.Stuff.Value, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The silver the goods already on the way are worth against the gap: the held stock of each export product
    /// (gear counts only its surplus), the packed sculptures and the pieces of the standing bills, each at the
    /// expected sale price.
    /// </summary>
    private static double ExportInFlight(ExportRequest r, List<ExportRecipe> recipes, List<GearBill> bills)
    {
        var price = new Dictionary<Resource, double>();
        var cheapestSculpture = 0.0;
        foreach (var e in recipes)
        {
            if (!r.Items.Market.TryGetValue(e.Product, out var baseValue))
            {
                continue;
            }
            price[e.Product] = ExportSellRatio * baseValue;
            if (e.Sculpture && (cheapestSculpture == 0 || price[e.Product] < cheapestSculpture))
            {
                cheapestSculpture = price[e.Product];
            }
        }
        var total = 0.0;
        var held = new Dictionary<Resource, long>();
        foreach (var product in price.Keys)
        {
            held[product] = r.Stock.GetValueOrDefault(product);
        }
        if (r.SurplusKnown)
        {
            foreach (var definition in price.Keys)
            {
                if (ExportGear(r.Items, definition))
                {
                    held[definition] = r.Surplus.Weapons.GetValueOrDefault(definition);
                }
            }
            foreach (var row in r.Surplus.Apparel)
            {
                if (price.ContainsKey(row.Definition))
                {
                    held[row.Definition] = held.GetValueOrDefault(row.Definition) + row.Count;
                }
            }
        }
        else
        {
            foreach (var definition in price.Keys)
            {
                if (ExportGear(r.Items, definition))
                {
                    held[definition] = 0;
                }
            }
        }
        foreach (var (product, n) in held)
        {
            total += n * price[product];
        }
        if (r.Packed.TryGetValue(out var packed) && packed > 0)
        {
            total += packed * cheapestSculpture;
        }
        foreach (var bill in bills)
        {
            if (bill.Spec.TryGetValue(out var spec) && bill.Products.Count == 1)
            {
                total += spec.Target * price.GetValueOrDefault(bill.Products[0]);
            }
        }
        return total;
    }

    /// <summary>The ingredient units a standing batch still draws: its pieces times one piece's picks under its ingredient filter.</summary>
    private static List<Amount> ExportBillDraw(List<ExportRecipe> recipes, OrderSpec spec)
    {
        foreach (var e in recipes)
        {
            if (e.Recipe.Definition != spec.Recipe || e.Bench.Def != spec.BenchKind)
            {
                continue;
            }
            var filter = spec.Ingredients.Select(s => new Resource(s)).ToList();
            var picks = ExportPicks(e.Slots, filter, default);
            if (picks is null)
            {
                return [];
            }
            return [.. picks.Select(a => new Amount { Resource = a.Resource, Count = a.Count * spec.Target })];
        }
        return [];
    }
}
