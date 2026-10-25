using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
namespace RimGovernor.Brain.Bridge;

/// <summary>
/// The stock-arrival model's read boundary (#3868): the catalog's stock recipes with the research finished, the bills census's
/// benches, the colonists who may craft and the stock on hand, and the trader visits (#3869: the storyteller's trader comps, the
/// trader kinds' stock, the threat section's TradeVisitFacts and today's silver), in plain values, built once a review into
/// <see cref="StockArrival"/> (<see cref="StockArrivalRules.Craft"/>, <see cref="TradeArrivalRules.Sources"/>). A census the frame
/// did not carry leaves the model unread, named in <see cref="StockArrival.Unread"/>.
/// </summary>
public static class StockArrivalReads
{
    public static StockArrival Of(DefinitionCatalog catalog, IReadOnlySet<string> finished, RimGovernor.Brain.GameFacts.Observations.BillsSnapshot? bills, RimGovernor.Brain.GameFacts.Observations.PawnSnapshot? pawns,
        RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot colony, IReadOnlyList<Resource> medicines)
    {
        if (bills is null)
        {
            return new StockArrival { Unread = "stock_arrival_bills" };
        }

        // A colony without colonists has no pawn census and no crafter.
        if (pawns is null && colony.ColonistCount != 0)
        {
            return new StockArrival { Unread = "stock_arrival_pawns" };
        }

        var stock = new Dictionary<Resource, long>();
        foreach (var q in colony.Resources)
        {
            var def = new Resource(q.DefName);
            stock[def] = stock.GetValueOrDefault(def) + q.Units;
        }

        var crafters = (pawns?.Pawns ?? []).Where(row => row.Colonist && !row.Dead && !row.Downed && !row.Drafted
            && row.MentalState is null && row.Settings.Work is not RimGovernor.Brain.GameFacts.Observations.PawnWork.NeverWorks).ToArray();
        var craft = StockArrivalRules.Craft(catalog.StockRecipes(finished), bills.Benches, crafters, stock);
        if (craft.Unread.Length != 0)
        {
            return craft;
        }

        var (trade, findings, unread) = Trade(catalog, colony.Threat.Observed, stock.GetValueOrDefault(new Resource(catalog.Derived.CurrencyDef)), colony.ColonistCount);
        return unread.Length != 0
            ? new StockArrival { Unread = unread }
            : craft with
            {
                Trade = EquatableArray.From(trade),
                Requests = ForecastTradeRequests.Options(catalog.TraderStock(), colony.Threat.Observed.Trade ?? throw new RequiredFactException("trader visit observation"),
                    TradePolicyRules.SpendableSilver(stock.GetValueOrDefault(new Resource(catalog.Derived.CurrencyDef)), colony.ColonistCount)),
                Findings = EquatableArray.From(craft.Findings.Concat(findings).Distinct().Order(StringComparer.Ordinal)),
                Medicines = EquatableArray.From(medicines),
            };
    }

    // The trader-visit sources (#3869): the storyteller's decoded trader comps, the trader kinds' stock, the visit facts the native
    // read carries, the colony's wealth and raid points, and the silver above the routine trade's reserve (#3855 Q2 A, #3907): the map's
    // for caravans and visitors, the silver within trade-beacon range for orbital traders (#3910: the routine trade's reserve holds
    // against the sheet's silver, which for an orbital trader is the beacon's).
    private static (IReadOnlyList<TradeSource> Sources, IReadOnlyList<string> Findings, string Unread) Trade(DefinitionCatalog catalog, RimGovernor.Brain.GameFacts.Observations.ThreatFacts threat,
        long silver, long colonists)
    {
        var facts = threat.Trade ?? throw new RequiredFactException("trader visit observation");

        var schedule = catalog.TradeSchedule();
        if (!schedule.Storytellers.TryGetValue(threat.Storyteller, out var storyteller))
        {
            return ([], [], "storyteller_unknown:" + threat.Storyteller);
        }

        return TradeArrivalRules.Sources(schedule, storyteller, catalog.TraderStock(), facts, threat.WealthTotal,
            TradePolicyRules.SpendableSilver(silver, colonists), TradePolicyRules.SpendableSilver(facts.BeaconSilver, colonists), threat.Cycles);
    }
}
