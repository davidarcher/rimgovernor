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
    public static FoodSupply? DecodeFoodSupply(O.FoodSupplyFacts? v, Things things, DefinitionCatalog? catalog)
    {
        if (v is null || FoodSupplyRules.JoinFoodSupply(v, things) is not { } rows || catalog is null)
        {
            return null;
        }
        return DecodeFoodSupply(v, rows, catalog);
    }

    private static FoodDefFacts FoodDefFactsOf(DefinitionCatalog catalog, string def, bool corpse) =>
        new(catalog.RawFoodClass(def), catalog.RawMeat(def), catalog.Vegetable(def), corpse && catalog.HumanlikeCorpse(def));

    private static FoodSupply DecodeFoodSupply(O.FoodSupplyFacts v, IReadOnlyDictionary<string, O.Thing> rows, DefinitionCatalog catalog)
    {
        var supply = new FoodSupply { Complete = Known(true) };
        if (v.Larder is { } l)
        {
            var corpses = new List<CorpseHandling>(l.Corpses.Count);
            foreach (var row in l.Corpses)
            {
                corpses.Add(new CorpseHandling
                {
                    InStorage = ColonyDecodeReads.Optional(row.InStorage is not null, row.InStorage.GetValueOrDefault()),
                    Id = row.StockId,
                    Cell = ColonyDecodeReads.CellOf(row.Cell),
                    Hauler = new PawnId((row.HaulerId ?? "")),
                    FrozenDestination = row.FrozenDestination,
                });
            }
            supply = supply with
            {
                Larder = Known(new FoodLarder
                {
                    RawMeatNutrition = l.RawMeatNutrition,
                    CookDemandNutrition = l.CookDemandNutrition,
                    Corpses = EquatableArray.From(corpses),
                }),
            };
        }
        var consumers = new List<FoodConsumer>(v.Consumers.Count);
        foreach (var row in v.Consumers)
        {
            consumers.Add(new FoodConsumer
            {
                Id = new PawnId((row.PawnId ?? "")),
                NutritionPerDay = ColonyDecodeReads.Optional(row.NutritionPerDay is not null, row.NutritionPerDay.GetValueOrDefault()),
                HumanMeatAcceptable = ColonyDecodeReads.Optional(row.HumanMeatAcceptable is not null, row.HumanMeatAcceptable.GetValueOrDefault()),
            });
        }
        var stocks = new List<FoodStock>();
        var barredStocks = new List<FoodStock>();
        foreach (var s in v.Stocks)
        {
            rows.TryGetValue(s.Item?.Id ?? "", out var row);
            row ??= new O.Thing();
            var def = row.Entity.DefName;
            var defFacts = FoodDefFactsOf(catalog, def, row.Corpse);
            // A thing rots exactly while it has a rot deadline.
            var stock = new FoodStock
            {
                Id = s.Item?.Id ?? "",
                IsHumanMeat = row.IsHumanMeat.GetValueOrDefault(),
                RawMeat = defFacts.RawMeat,
                IsHumanlike = defFacts.Humanlike,
                Vegetable = defFacts.Vegetable,
                RawClass = Known(defFacts.Class),
                Holder = Known(new PawnId(s.Holder?.Id ?? "")),
                Nutrition = ColonyDecodeReads.Optional(s.Nutrition is not null, s.Nutrition.GetValueOrDefault()),
                Perishable = Known(row.RotTicks is not null),
                RotTicks = ColonyDecodeReads.Optional(row.RotTicks is not null, row.RotTicks.GetValueOrDefault()),
                DefName = new Resource(def),
                Roofed = ColonyDecodeReads.Optional(row.Roofed is not null, row.Roofed.GetValueOrDefault()),
                TemperatureC = ColonyDecodeReads.Optional(row.TemperatureC is not null, row.TemperatureC.GetValueOrDefault()),
                Room = ColonyDecodeReads.OptionalRef(row.Room),
            };
            stock = stock with { Count = Known(row.StackCount) };
            try
            {
                stock = stock with { UnitMass = PlanningViewReads.FiniteFact((float)catalog.StatValue(def, "", CatalogStats.Mass)) };
            }
            catch (DomainException)
            {
                // A def with no readable mass leaves the unit mass unknown.
            }
            stock = stock with { Corpse = row.Corpse };
            // A forbidden stack is the travel reserve when its def is one; any other forbidden stack has no
            // eaters and counts only as human meat.
            var barred = false;
            if (row.Forbidden && !stock.Corpse)
            {
                var reserve = FoodReserveRules.ReserveFoodDefinition(stock.DefName);
                stock = stock with { Reserve = reserve };
                barred = !reserve;
            }
            stock = stock with
            {
                Forbidden = Known(row.Forbidden),
                MeatAmount = ColonyDecodeReads.Optional(row.MeatAmount is not null, row.MeatAmount.GetValueOrDefault()),
                BodySize = ColonyDecodeReads.Optional(row.BodySize is not null, row.BodySize.GetValueOrDefault()),
                TileFootprint = ColonyDecodeReads.Optional(row.TileFootprint is not null, (long)row.TileFootprint.GetValueOrDefault()),
            };
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
        return supply with { Consumers = consumers, Stocks = stocks, Barred = barredStocks };
    }
}
