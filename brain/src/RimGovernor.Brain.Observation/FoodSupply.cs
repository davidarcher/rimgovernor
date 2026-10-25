using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Observation;

/// <summary>
/// What a food stock's def says about it: its raw ingredient class (a raw meat is the meat class), whether its
/// food type includes vegetable or fruit, and for a corpse whether its race is humanlike.
/// </summary>
internal readonly record struct FoodDefFacts(FoodIngredientClass Class, bool RawMeat, bool Vegetable, bool Humanlike);

public static class FoodSupplyReads
{
    /// <summary>
    /// Joins <paramref name="v"/>'s stocks to their things table rows and to their def rows in the definition
    /// catalog; null when the table misses a stock or no catalog is held, and the supply is then unknown until a
    /// later frame. A stock whose def the catalog lacks is a contract error.
    /// </summary>
    public static FoodSupply? DecodeFoodSupply(O.FoodSupplyFacts v, Things things, DefinitionCatalog? catalog)
    {
        if (FoodSupplyRules.JoinFoodSupply(v, things) is not { } rows || catalog is null)
        {
            return null;
        }
        return DecodeFoodSupply(v, rows, catalog);
    }

    private static FoodDefFacts FoodDefFactsOf(DefinitionCatalog catalog, string def, bool corpse) =>
        new(catalog.RawFoodClass(def), catalog.RawMeat(def), catalog.Vegetable(def), corpse && catalog.HumanlikeCorpse(def));

    private static FoodSupply DecodeFoodSupply(O.FoodSupplyFacts v, IReadOnlyDictionary<string, O.Thing> rows, DefinitionCatalog catalog)
    {
        var corpses = new List<CorpseHandling>(v.Larder.Corpses.Count);
        foreach (var row in v.Larder.Corpses)
        {
            corpses.Add(new CorpseHandling
            {
                Id = row.StockId,
                Cell = ColonyDecodeReads.CellOf(row.Cell),
                Hauler = new PawnId((row.HaulerId ?? "")),
                FrozenDestination = row.FrozenDestination,
            });
        }
        var larder = new FoodLarder
        {
            RawMeatNutrition = v.Larder.RawMeatNutrition,
            CookDemandNutrition = v.Larder.CookDemandNutrition,
            Corpses = EquatableArray.From(corpses),
        };
        var consumers = new List<FoodConsumer>(v.Consumers.Count);
        foreach (var row in v.Consumers)
        {
            consumers.Add(new FoodConsumer
            {
                Id = new PawnId(row.PawnId),
                NutritionPerDay = row.NutritionPerDay,
                HumanMeatAcceptable = row.HumanMeatAcceptable,
            });
        }
        var stocks = new List<FoodStock>();
        var barredStocks = new List<FoodStock>();
        foreach (var s in v.Stocks)
        {
            // The join holds every stock's row, and a stock's row carries the food facts of an ingestible or a corpse.
            var row = rows[s.Item.Id ?? ""];
            var def = row.Entity.DefName;
            var defFacts = FoodDefFactsOf(catalog, def, row.Corpse);
            var food = $"food stock {s.Item.Id}";
            var edible = row.Food as RimGovernor.Contracts.Wire.Observations.ThingFood.Edible ?? throw new ObservationContractException($"{food} is not an ingestible item or a corpse");
            FoodForm form = row.Corpse
                ? new FoodForm.Corpse((edible.Yield ?? throw new ObservationContractException($"{food} is a corpse without its yield")).MeatAmount, edible.Yield.BodySize)
                : new FoodForm.Item();
            // A spawned stock stands where its roof is read; a stock in an inventory is carried.
            FoodPlace place = edible.Place is RimGovernor.Contracts.Wire.Observations.ThingPlace.Stands stands ? new FoodPlace.Stands(stands.Roofed, stands.Room?.Id ?? "") : new FoodPlace.Carried();
            // A thing rots exactly while it has a rot deadline.
            var stock = new FoodStock
            {
                Id = s.Item.Id ?? "",
                IsHumanMeat = edible.IsHumanMeat,
                RawMeat = defFacts.RawMeat,
                IsHumanlike = defFacts.Humanlike,
                Vegetable = defFacts.Vegetable,
                RawClass = defFacts.Class,
                Holder = new PawnId(s.Holder?.Id ?? ""),
                Nutrition = s.Nutrition,
                Rot = edible.Rot is RimGovernor.Contracts.Wire.Observations.ThingRot.Rots rot ? new FoodRot.Rots(rot.Ticks) : new FoodRot.Keeps(),
                DefName = new Resource(def),
                Place = place,
                TemperatureC = edible.TemperatureC,
                Count = row.StackCount,
                UnitMass = catalog.StatValue(def, "", CatalogStats.Mass),
                Form = form,
                Forbidden = row.Forbidden,
            };
            // A forbidden stack is the travel reserve when its def is one; any other forbidden stack has no
            // eaters and counts only as human meat.
            var barred = false;
            if (row.Forbidden && !stock.Corpse)
            {
                var reserve = FoodReserveRules.ReserveFoodDefinition(stock.DefName);
                stock = stock with { Reserve = reserve };
                barred = !reserve;
            }
            if (barred && !stock.IsHumanMeat)
            {
                // Census-only row (Barred): no eaters, outside the forecast.
                barredStocks.Add(stock);
                continue;
            }
            if (!barred)
            {
                var eaters = new List<PawnId>();
                foreach (var id in FactHelpers.RefIds(s.Eaters))
                {
                    eaters.Add(new PawnId(id));
                }
                stock = stock with { Eaters = eaters };
            }
            stocks.Add(stock);
        }
        return new FoodSupply { Consumers = consumers, Stocks = stocks, Barred = barredStocks, Larder = larder };
    }
}
