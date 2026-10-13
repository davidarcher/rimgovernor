namespace RimGovernor.Brain.Policy;

// Go: acquisition_catalog.go
// The acquisition method catalog: every way the colony can obtain a resource is a SupplyCandidate of one kind,
// ranked by PlanSupply against the demand instead of each goal hardcoding its own order (produce before mine,
// forage before hunt).

public static partial class PolicyFunctions
{
    /// <summary>
    /// Each kind's estimated pawn work ticks per unit yielded: a planning prior for the kinds whose per-unit
    /// work native does not report, so a bill and a deposit covering the same deficit compare. Measured
    /// distance, trips and risk refine a candidate on top of it.
    /// </summary>
    public static readonly IReadOnlyDictionary<CandidateKind, double> AcquisitionLaborPerUnit = new Dictionary<CandidateKind, double>
    {
        [CandidateKind.Harvest] = 10,
        [CandidateKind.Chop] = 15,
        [CandidateKind.Mining] = 20,
        [CandidateKind.Salvage] = 25,
        [CandidateKind.Loot] = 5,
        [CandidateKind.Hunt] = 30,
        [CandidateKind.Produce] = 60,
        [CandidateKind.DeepDrill] = 100,
        [CandidateKind.Trade] = 0,
    };

    /// <summary>One pawn's haul of a stackable resource when no carry capacity was observed.</summary>
    internal const long AcquisitionUnitsPerTrip = 75;

    /// <summary>Prices a purchase's silver as labor, so a caravan's dear goods can lose to a near deposit.</summary>
    internal const double TradeLaborPerSilver = 1.0;

    /// <summary>Bounds one acquisition method, matching SelectResourceSources' native selection cap.</summary>
    public const int MaxCatalogSelection = 8;

    internal static Fact<double> CatalogLabor(CandidateKind kind, long units) =>
        Known(AcquisitionLaborPerUnit.GetValueOrDefault(kind) * Math.Max(units, 1));

    /// <summary>The one-row demand a MaintainResource floor ranks candidates against.</summary>
    public static Fact<EquatableArray<ResourceDemand>> ResourceDeficitDemand(Resource resource, long deficit)
    {
        if (deficit <= 0)
        {
            return Known(new EquatableArray<ResourceDemand>());
        }

        return Known(EquatableArray.From([new ResourceDemand { Key = new ResourceKey { Def = resource }, Count = deficit, Priority = 1 }]));
    }

    /// <summary>The catalog rows of selected native mine sources; headroom is the storage accepting the output.</summary>
    public static IReadOnlyList<SupplyCandidate> MineCandidates(Resource resource, IEnumerable<ResourceSource> sources, Fact<long> headroom)
    {
        var output = new List<SupplyCandidate>();
        foreach (var s in sources)
        {
            if (s.Method != ResourceSourceMethod.Mine || s.Yield <= 0)
            {
                continue;
            }

            output.Add(
                SourceCandidate(
                    CandidateKind.Mining,
                    s.ThingID,
                    CatalogLabor(CandidateKind.Mining, s.Yield),
                    Known(s.Distance),
                    true,
                    AcquisitionUnitsPerTrip,
                    SourceYield(new ResourceKey { Def = resource }, s.Yield, 0, headroom)
                )
            );
        }

        return output;
    }

    /// <summary>
    /// The catalog row of a chosen production bill covering units: the product drops at the bench, so no haul is
    /// charged. Every ingredient of the recipe is its upfront resource cost, so the plan prices the bill against
    /// the stock the colony holds (UsableIngredients).
    /// </summary>
    public static bool ProduceCandidate(ResourceMethod m, long units, out SupplyCandidate candidate)
    {
        if (m.Kind != ResourceMethodKind.Produce || units <= 0)
        {
            candidate = new SupplyCandidate();
            return false;
        }

        var c = SourceCandidate(
            CandidateKind.Produce,
            m.Bench + "/" + m.Recipe,
            CatalogLabor(CandidateKind.Produce, units),
            Known(0.0),
            false,
            0,
            SourceYield(new ResourceKey { Def = m.Resource }, units, 0, Unknown<long>())
        );
        var resources = new List<ResourceQuantity>(c.UpfrontCost.Resources);
        foreach (var ingredient in m.Ingredients)
        {
            resources.Add(new ResourceQuantity { Key = new ResourceKey { Def = ingredient.Resource }, Count = ingredient.Count * units });
        }

        candidate = c with { UpfrontCost = c.UpfrontCost with { Resources = EquatableArray.From(resources) } };
        return true;
    }

    /// <summary>
    /// Per supplied ingredient, the stock a bill may spend: the usable census (Available), or the runway row's
    /// own stock when lower, less the protected line: the row's reserve plus its observed use over the
    /// projection horizon, and nothing for an ingredient without a row or with an unread rate. An ingredient
    /// whose census is unobserved is left out, so a bill that draws on it is unknown. Prospective ore never
    /// funds a bill.
    /// </summary>
    public static IReadOnlyList<ResourceQuantity> UsableIngredients(IReadOnlyList<ResourceRunway> runways, IEnumerable<Stock> supply)
    {
        var output = new List<ResourceQuantity>();
        foreach (var s in supply)
        {
            if (!s.Available.TryGetValue(out var have) || have < 0)
            {
                continue;
            }

            var line = 0.0;
            foreach (var row in runways)
            {
                if (row.Resource != s.Resource)
                {
                    continue;
                }

                if (row.Stock.TryGetValue(out var stock) && stock >= 0)
                {
                    have = Math.Min(have, stock);
                }

                line = Math.Max(0, row.Reserve);
                if (row.ConsumptionPerDay.TryGetValue(out var rate) && Finite(rate) && rate > 0)
                {
                    line += Math.Ceiling(rate * ProjectionHorizonDays);
                }
            }

            output.Add(new ResourceQuantity { Key = new ResourceKey { Def = s.Resource }, Count = (long)Math.Max(0, have - line) });
        }

        return output;
    }

    /// <summary>
    /// The catalog rows of the colony-facts acquisition census (trees, wild plants, animals) yielding resource:
    /// a tree is chop, a hunt is hunt, anything else harvest. A hunt's revenge chance is its risk: labor scales
    /// by 1+2*chance. Distance is straight from home, the colony's reference cell.
    /// </summary>
    public static IReadOnlyList<SupplyCandidate> AcquisitionSourceCandidates(
        Resource resource,
        IEnumerable<AcquisitionSource> sources,
        Cell home,
        Fact<long> headroom
    )
    {
        var output = new List<SupplyCandidate>();
        foreach (var s in sources)
        {
            var units = (long)Math.Round(s.Yield, MidpointRounding.AwayFromZero);
            if (new Resource(s.Resource) != resource || units <= 0)
            {
                continue;
            }

            var kind = CandidateKind.Harvest;
            var risk = 0.0;
            if (s.Hunt)
            {
                kind = CandidateKind.Hunt;
                risk = Math.Min(1, Math.Max(0, s.RevengeChance));
            }
            else if (s.Tree)
            {
                kind = CandidateKind.Chop;
            }

            CatalogLabor(kind, units).TryGetValue(out var labor);
            output.Add(
                SourceCandidate(
                    kind,
                    s.ID,
                    Known(labor * (1 + (2 * risk))),
                    Known(Math.Sqrt(Math.Pow(s.Cell.X - home.X, 2) + Math.Pow(s.Cell.Z - home.Z, 2))),
                    true,
                    AcquisitionUnitsPerTrip,
                    SourceYield(new ResourceKey { Def = resource }, units, 0, headroom)
                )
            );
        }

        return output;
    }

    /// <summary>The catalog row of a drill over a deep lump: units is the lump's yield toward the deficit, distance from home.</summary>
    public static bool DeepDrillCandidate(Resource resource, string id, long units, double distance, Fact<long> headroom, out SupplyCandidate candidate)
    {
        if (units <= 0)
        {
            candidate = new SupplyCandidate();
            return false;
        }

        candidate = SourceCandidate(
            CandidateKind.DeepDrill,
            id,
            CatalogLabor(CandidateKind.DeepDrill, units),
            Known(distance),
            true,
            AcquisitionUnitsPerTrip,
            SourceYield(new ResourceKey { Def = resource }, units, 0, headroom)
        );
        return true;
    }

    /// <summary>
    /// The catalog row of buying units of resource from a caravan at price silver each; the goods drop at the
    /// colony, so no haul is charged. Its ID names the trader and the resource (TradeCandidateID), one row per pair.
    /// </summary>
    public static bool TradeCandidate(Resource resource, string trader, long units, double price, out SupplyCandidate candidate)
    {
        if (units <= 0 || !(price >= 0))
        {
            candidate = new SupplyCandidate();
            return false;
        }

        candidate = SourceCandidate(
            CandidateKind.Trade,
            TradeCandidateID(trader, resource),
            Known(price * units * TradeLaborPerSilver),
            Known(0.0),
            false,
            0,
            SourceYield(new ResourceKey { Def = resource }, units, 0, Unknown<long>())
        );
        return true;
    }
}
