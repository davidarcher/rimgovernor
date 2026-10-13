namespace RimGovernor.Brain.Observation;

/// <summary>
/// What a food stock's def says about it: its raw ingredient class (a raw meat is the meat class), whether its
/// food type includes vegetable or fruit, and for a corpse whether its race is humanlike.
/// </summary>
internal readonly record struct FoodDefFacts(FoodIngredientClass Class, bool RawMeat, bool Vegetable, bool Humanlike);

public static partial class ObservationPackage
{
    /// <summary>
    /// Joins <paramref name="v"/>'s stocks to their things table rows and to their def rows in the definition
    /// catalog; null when the table misses a stock or no catalog is held, and the supply is then unknown until a
    /// later frame. A stock whose def the catalog lacks is a contract error.
    /// </summary>
    public static FoodSupply? DecodeFoodSupply(O.FoodSupplyFacts? v, Things things, DefinitionCatalog? catalog)
    {
        if (v is null || !JoinFoodSupply(v, things, out var rows) || catalog is null)
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
                    InStorage = Optional(row.HasInStorage, row.InStorage),
                    Id = row.StockId,
                    Cell = CellOf(row.Cell),
                    Hauler = new PawnId(row.HaulerId),
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
                Id = new PawnId(row.PawnId),
                NutritionPerDay = Optional(row.HasNutritionPerDay, row.NutritionPerDay),
                HumanMeatAcceptable = Optional(row.HasHumanMeatAcceptable, row.HumanMeatAcceptable),
            });
        }
        var stocks = new List<FoodStock>();
        var barredStocks = new List<FoodStock>();
        foreach (var s in v.Stocks)
        {
            rows.TryGetValue(s.Item?.Id ?? "", out var row);
            row ??= new O.Thing();
            var def = row.Thing?.DefName ?? "";
            var defFacts = FoodDefFactsOf(catalog, def, row.Corpse);
            // A thing rots exactly while it has a rot deadline.
            var stock = new FoodStock
            {
                Id = s.Item?.Id ?? "",
                IsHumanMeat = row.IsHumanMeat,
                RawMeat = defFacts.RawMeat,
                IsHumanlike = defFacts.Humanlike,
                Vegetable = defFacts.Vegetable,
                RawClass = Known(defFacts.Class),
                Holder = Known(new PawnId(s.Holder?.Id ?? "")),
                Nutrition = Optional(s.HasNutrition, s.Nutrition),
                Perishable = Known(row.HasRotTicks),
                RotTicks = Optional(row.HasRotTicks, row.RotTicks),
                DefName = new Resource(def),
                Roofed = Optional(row.HasRoofed, row.Roofed),
                TemperatureC = Optional(row.HasTemperatureC, row.TemperatureC),
                Room = OptionalRef(row.Room),
            };
            if (row.HasStackCount)
            {
                stock = stock with { Count = Known((long)row.StackCount) };
            }
            try
            {
                stock = stock with { UnitMass = FiniteFact((float)catalog.StatValue(def, "", "Mass")) };
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
                var reserve = PolicyFunctions.ReserveFoodDefinition(stock.DefName);
                stock = stock with { Reserve = reserve };
                barred = !reserve;
            }
            stock = stock with
            {
                Forbidden = Optional(row.HasForbidden, row.Forbidden),
                MeatAmount = Optional(row.HasMeatAmount, row.MeatAmount),
                BodySize = Optional(row.HasBodySize, row.BodySize),
                TileFootprint = Optional(row.HasTileFootprint, (long)row.TileFootprint),
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
                foreach (var id in RefIds(s.Eaters))
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
