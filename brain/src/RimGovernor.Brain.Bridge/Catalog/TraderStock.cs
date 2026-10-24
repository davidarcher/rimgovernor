using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// What each trader kind's stock generators make of the stocks a rule waits on (#3869 of #3650): the parts an install recipe takes
// and the medicines (Catalog/StockRecipes.cs). ThingSetMaker_TraderStock runs every generator of the kind; a generator is matched
// by its exact class, as StockGenerator_X.GenerateThings picks its defs:
//   - StockGenerator_SingleDef: its thingDef;
//   - StockGenerator_MultiDef: thingDefs.RandomElement();
//   - StockGenerator_Category: thingDefCountRange distinct defs drawn uniformly from categoryDef.DescendantThingDefs that the trader
//     can sell (Tradeability.All or Buyable), at most maxTechLevelGenerate, not excluded by def or category;
//   - StockGenerator_Tag: thingDefCountRange distinct defs drawn by SelectionWeight (1) from the defs HandlesThingDef takes (the
//     tradeTag, tradeability not None, at most maxTechLevelBuy) that the trader can sell, the player can acquire, not excluded;
// each then makes StockGenerator.RandomCountOf of the def (P.StockDraw). The Buy generators make nothing (GenerateThings is empty);
// any other class that handles a stock resource the trader can sell is named unmodelled. The unit value is TradeUtility's
// PriceFactorBuy_Global × the def's base market value × TraderKindDef.PriceTypeFor(def, PlayerBuys).PriceMultiplier() (kept
// constants: "Mirrored game rules: trader stock").

public sealed partial class DefinitionCatalog
{
    private static readonly HashSet<string> MakesNoStock = new(StringComparer.Ordinal)
    {
        nameof(StockGenerator_BuyExpensiveSimple), nameof(StockGenerator_BuySingleDef), nameof(StockGenerator_BuySlaves), nameof(StockGenerator_BuyTradeTag),
    };

    private Lazy<P.TraderStockTable>? _traderStock;

    /// <summary>Every trader kind's stock of the stock-arrival resources (#3869, <see cref="P.TraderKindStock"/>).</summary>
    public P.TraderStockTable TraderStock() => (_traderStock ??= new(BuildTraderStock)).Value;

    private P.TraderStockTable BuildTraderStock()
    {
        var parts = InstallParts();
        var things = Rows<ThingDef>().Values.OrderBy(d => d.defName, StringComparer.Ordinal).ToList();
        var resources = things.Where(d => parts.Contains(d.defName) || Medicine(d.defName)).ToList();
        return new P.TraderStockTable
        {
            Kinds = Rows<TraderKindDef>().ToDictionary(row => row.Key, row => TraderKindStockOf(row.Value, resources, things), StringComparer.Ordinal),
        };
    }

    private P.TraderKindStock TraderKindStockOf(TraderKindDef kind, List<ThingDef> resources, List<ThingDef> things)
    {
        var draws = resources.ToDictionary(r => r.defName, _ => new List<P.StockDraw>(), StringComparer.Ordinal);
        var unmodelled = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var generator in kind.stockGenerators.Seq())
        {
            var name = generator?.GetType().Name ?? "null";
            switch (generator)
            {
                case StockGenerator_SingleDef single when name == nameof(StockGenerator_SingleDef):
                    foreach (var def in resources.Where(d => d == GameMembers.SingleDefThing(single) && TraderCanSell(d.tradeability)))
                    {
                        draws[def.defName].Add(Draw(single, def, 1, 1, 1, 1));
                    }

                    break;
                case StockGenerator_MultiDef multi when name == nameof(StockGenerator_MultiDef):
                    var listed = GameMembers.MultiDefThings(multi).Seq().ToList();
                    foreach (var def in resources.Where(d => listed.Contains(d) && TraderCanSell(d.tradeability)))
                    {
                        draws[def.defName].Add(Draw(multi, def, listed.Count, Enumerable.Count(listed, d => d == def), 1, 1));
                    }

                    break;
                case StockGenerator_Category category when name == nameof(StockGenerator_Category):
                    var within = GameMembers.StockCategory(category)?.defName;
                    var excluded = GameMembers.StockExcludedThings(category).Seq().ToHashSet();
                    var excludedCategories = GameMembers.StockExcludedCategories(category).Seq().Select(c => c.defName).ToHashSet(StringComparer.Ordinal);
                    var pool = things.Where(d => CategoriesWithin(d) is var cats && within is not null && cats.Contains(within) && !cats.Overlaps(excludedCategories)
                        && TraderCanSell(d.tradeability) && d.techLevel <= category.maxTechLevelGenerate && !excluded.Contains(d)).ToHashSet();
                    var count = GameMembers.StockCategoryDefCount(category);
                    foreach (var def in resources.Where(pool.Contains))
                    {
                        draws[def.defName].Add(Draw(category, def, pool.Count, 1, count.min, count.max));
                    }

                    break;
                case StockGenerator_Tag tag when name == nameof(StockGenerator_Tag):
                    var excludedTags = GameMembers.StockTagExcludedThings(tag).Seq().ToHashSet();
                    var tagged = things.Where(d => d.tradeTags is { } tags && tags.Contains(tag.tradeTag) && d.tradeability != Tradeability.None
                        && d.techLevel <= tag.maxTechLevelBuy && TraderCanSell(d.tradeability) && Acquirable(d) && !excludedTags.Contains(d)).ToHashSet();
                    var defs = GameMembers.StockTagDefCount(tag);
                    foreach (var def in resources.Where(tagged.Contains))
                    {
                        draws[def.defName].Add(Draw(tag, def, tagged.Count, 1, defs.min, defs.max));
                    }

                    break;
                default:
                    if (!MakesNoStock.Contains(name) && (generator is null || resources.Any(d => TraderCanSell(d.tradeability) && generator.HandlesThingDef(d))))
                    {
                        unmodelled.Add(name);
                    }

                    break;
            }
        }

        var offers = new Dictionary<P.Resource, P.TraderOffer>();
        foreach (var def in resources.Where(d => draws[d.defName].Count > 0))
        {
            var resource = new P.Resource(def.defName);
            offers[resource] = BaseMarketValue(def) is { } value
                ? new P.TraderOffer
                {
                    UnitValue = Decimal32((float)value * TradeUtility.PriceFactorBuy_Global * PriceMultiplier(PlayerBuysPrice(kind, def))),
                    Draws = EquatableArray.From(draws[def.defName]),
                }
                : new P.TraderOffer { Unread = "market_value:" + def.defName };
        }

        return new P.TraderKindStock { Favor = kind.tradeCurrency == TradeCurrency.Favor, Offers = offers, Unmodelled = [.. unmodelled] };
    }

    // One generator's draw of the def: the pick, its RandomCountOf ranges (a customCountRanges row of the def overrides countRange)
    // and the def's base market value (NaN unshown: its offer is then unread and never priced).
    private P.StockDraw Draw(StockGenerator generator, ThingDef def, int pool, int copies, int defsMin, int defsMax)
    {
        var counts = generator.customCountRanges.Seq().FirstOrDefault(c => c?.thingDef == def)?.countRange ?? generator.countRange;
        return new P.StockDraw(pool, copies, defsMin, defsMax, counts.min, counts.max, Decimal32(generator.totalPriceRange.min), Decimal32(generator.totalPriceRange.max),
            BaseMarketValue(def) ?? double.NaN);
    }

    // TraderKindDef.PriceTypeFor(def, TradeAction.PlayerBuys) of a def that is not the currency (a stock resource never is): the
    // price of the first generator whose HandlesThingDef takes it (StockGenerator.TryGetPriceType), else Normal. Mirrored to skip
    // its ThingDefOf.Silver test, a DefOf the catalog does not read.
    private static PriceType PlayerBuysPrice(TraderKindDef kind, ThingDef def) =>
        kind.stockGenerators.Seq().FirstOrDefault(g => g is not null && g.HandlesThingDef(def))?.price ?? PriceType.Normal;

    // ThingDef.BaseMarketValue: the def's MarketValue stat with no stuff; null when the game does not show it (the offer is then unread).
    private float? BaseMarketValue(ThingDef def) => ShownStatValue(def.defName, string.Empty, CatalogStats.MarketValue) is (var value, true) ? value : null;

    // TradeabilityUtility.TraderCanSell: the trader sells a def of Tradeability.All or Buyable.
    private static bool TraderCanSell(Tradeability tradeability) => tradeability is Tradeability.All or Tradeability.Buyable;

    // PriceTypeUtlity.PriceMultiplier (kept constants: "Mirrored game rules: trader stock").
    private static float PriceMultiplier(PriceType price) => price switch
    {
        PriceType.VeryCheap => 0.4f,
        PriceType.Cheap => 0.7f,
        PriceType.Normal => 1f,
        PriceType.Expensive => 2f,
        PriceType.Exorbitant => 5f,
        _ => throw new P.RequiredFactException("price_type:" + price),
    };

    // ThingDef.PlayerAcquirable without the world: a def destroyed on drop is not; ReinforcedBarrel under classic mortars and a def
    // requiring a faction absent from the world are world states the catalog does not read (the def stays in the pool).
    private static bool Acquirable(ThingDef def) => !def.destroyOnDrop;
}
