using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_trade.go and catalog_trade_buy.go: trader compatibility, read from the StockGenerator forms of a
// TraderKindDef. An unset countRange of the game is the zero range (generates nothing), where the Go mirror could tell
// it was unset; the answer for such a generator is "no", not unknown. A generator form the catalog cannot classify
// keeps a negative answer unknown.

public sealed partial class DefinitionCatalog
{
    private static bool IsBuyGenerator(StockGenerator generator) => generator.GetType().Name.Contains("_Buy", StringComparison.Ordinal);

    /// <summary>Sell-generator compatibility, never probability, stock or prices: whether a trader of kind can supply the good. Unsupported generator forms keep a negative answer unknown.</summary>
    public Fact<bool> TraderKindCanSupply(string kind, P.ResourceKey good)
    {
        if (DefRow<TraderKindDef>(kind) is not { } row)
        {
            return Unknown<bool>();
        }

        var items = ItemFacts();
        var names = new List<string> { good.Def.Value };
        if (good == NutritionKey)
        {
            names = [.. items.Nutrition.Where(pair => pair.Value > 0 && TradeFood(pair.Key.Value) is not null).Select(pair => pair.Key.Value)];
            if (names.Count == 0)
            {
                return Unknown<bool>();
            }
        }

        var unknown = false;
        foreach (var generator in row.stockGenerators.Seq())
        {
            if (generator is null)
            {
                unknown = true;
                continue;
            }

            if (IsBuyGenerator(generator))
            {
                continue;
            }

            if (generator is not (StockGenerator_SingleDef or StockGenerator_MultiDef or StockGenerator_Tag or StockGenerator_Category))
            {
                unknown = true;
                continue;
            }

            foreach (var name in names)
            {
                if (FindThing(name) is not { } def)
                {
                    unknown = true;
                    continue;
                }

                if (generator.countRange.max <= 0 || def.techLevel > generator.maxTechLevelGenerate)
                {
                    continue;
                }

                var compatible = false;
                switch (generator)
                {
                    case StockGenerator_SingleDef single:
                        compatible = GameMembers.SingleDefThing(single)?.defName == name;
                        break;
                    case StockGenerator_MultiDef multi:
                        compatible = GameMembers.MultiDefThings(multi).Seq().Any(d => d.defName == name);
                        break;
                    case StockGenerator_Tag tag:
                        compatible = def.tradeTags.Seq().Contains(tag.tradeTag) && !GameMembers.StockTagExcludedThings(tag).Seq().Any(d => d.defName == name);
                        break;
                    case StockGenerator_Category category:
                        if (!items.Categories.TryGetValue(new P.Resource(name), out var cats))
                        {
                            unknown = true;
                            continue;
                        }

                        compatible = !GameMembers.StockExcludedThings(category).Seq().Any(d => d.defName == name)
                            && cats.Contains(GameMembers.StockCategory(category)?.defName ?? string.Empty)
                            && !cats.Any(cat => GameMembers.StockExcludedCategories(category).Seq().Any(x => x.defName == cat));
                        break;
                }

                if (compatible)
                {
                    return Known(true);
                }
            }
        }

        return unknown ? Unknown<bool>() : Known(false);
    }

    /// <summary>
    /// Buy-generator compatibility: whether a trader of kind will buy def from the colony, as its
    /// StockGenerator_BuyTradeTag, _BuySingleDef and _BuyExpensiveSimple rows handle it. It reads neither the trader's
    /// cash nor its price. Unknown for an unread kind or def and for a generator form it cannot classify; a def the player
    /// cannot sell is a known no.
    /// </summary>
    public Fact<bool> TraderKindBuys(string kind, P.Resource def)
    {
        if (DefRow<TraderKindDef>(kind) is not { } row || FindThing(def.Value) is not { } thing)
        {
            return Unknown<bool>();
        }

        if (thing.tradeability is not (Tradeability.Sellable or Tradeability.All))
        {
            return Known(false);
        }

        var unknown = false;
        foreach (var generator in row.stockGenerators.Seq())
        {
            if (generator is null)
            {
                unknown = true;
                continue;
            }

            if (!IsBuyGenerator(generator))
            {
                continue;
            }

            switch (generator)
            {
                case StockGenerator_BuyTradeTag tag:
                    if (thing.techLevel <= tag.maxTechLevelBuy && thing.tradeTags.Seq().Contains(tag.tag))
                    {
                        return Known(true);
                    }

                    break;
                case StockGenerator_BuySingleDef single:
                    if (thing.techLevel <= single.maxTechLevelBuy && single.thingDef?.defName == def.Value)
                    {
                        return Known(true);
                    }

                    break;
                case StockGenerator_BuyExpensiveSimple expensive:
                    // A def with no base market value (a stuffed good) does not qualify by value: it is bought only
                    // through a tag or a def row.
                    if (thing.techLevel <= expensive.maxTechLevelBuy && ItemFacts().Market.TryGetValue(def, out var value) && value >= expensive.minValuePerUnit)
                    {
                        return Known(true);
                    }

                    break;
                case StockGenerator_BuySlaves:
                    break;
                default:
                    unknown = true;
                    break;
            }
        }

        return unknown ? Unknown<bool>() : Known(false);
    }

    /// <summary>The low end of the silver a trader of kind carries: the countRange minimum of its currency stock generator. Unknown when the kind has no currency row.</summary>
    public Fact<long> TraderKindSilver(string kind)
    {
        if (DefRow<TraderKindDef>(kind) is not { } row)
        {
            return Unknown<long>();
        }

        var currency = row.stockGenerators.Seq().OfType<StockGenerator_SingleDef>().Where(single => GameMembers.SingleDefThing(single)?.defName == Derived.CurrencyDef).ToList();
        return currency.Count == 0 ? Unknown<long>() : Known(currency.Sum(single => (long)single.countRange.min));
    }
}
