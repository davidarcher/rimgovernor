namespace RimGovernor.Brain.Observation;

/// <summary>
/// The stock-arrival model's read boundary (#3868): the catalog's stock recipes with the research finished, the bills census's
/// benches, the colonists who may craft and the stock on hand, and the trader visits (#3869: the storyteller's trader comps, the
/// trader kinds' stock, the threat section's TradeVisitFacts and today's silver), in plain values, built once a review into
/// <see cref="StockArrival"/> (<see cref="StockArrivalRules.Craft"/>, <see cref="TradeArrivalRules.Sources"/>). A census the frame
/// did not carry leaves the model unread, named in <see cref="StockArrival.Unread"/>.
/// </summary>
public static class StockArrivalReads
{
    public static StockArrival Of(DefinitionCatalog catalog, IReadOnlySet<string> finished, O.BillsSnapshot? bills, O.PawnSnapshot? pawns,
        O.ColonyFactsSnapshot colony, IReadOnlyList<Resource> medicines)
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

        if (ColonyDecodeReads.HasIssue(colony.Issues, "resources"))
        {
            return new StockArrival { Unread = "stock_arrival_resources" };
        }

        var stock = new Dictionary<Resource, long>();
        foreach (var q in colony.Resources)
        {
            var def = new Resource(q.DefName);
            stock[def] = stock.GetValueOrDefault(def) + q.Units;
        }

        var benches = bills.Benches.Select(b => new StockBench(b.BenchDef, b.Usable, b.WorkSpeed)).ToArray();
        var crafters = new List<StockCrafter>();
        foreach (var row in (pawns?.Pawns ?? []).Where(r => r.Colonist && !r.Dead && !r.Downed && !r.Drafted && r.MentalState is null && r.Settings.Work is not O.PawnWork.NeverWorks))
        {
            if (PawnRules.WorkHours([.. row.Settings.Schedule.Select(slot => slot.AssignmentDefName)]) is not { } hours)
            {
                return new StockArrival { Unread = "stock_arrival_schedule:" + row.Pawn.Id };
            }

            // An uninitialized tracker has no rows yet.
            var rows = row.Settings.Work is O.PawnWork.Works { Rows: var read } ? read : [];
            var works = rows.Where(w => w.Priority > 0 && !w.Disabled)
                .Select(w => ColonyDecodeReads.WireParseOpen(w.DefName, WorkType.Unrecognized)).Where(w => w != WorkType.Unrecognized);
            crafters.Add(new StockCrafter(row.Pawn.Id ?? "", hours, EquatableArray.From(works),
                EquatableArray.From(row.Biography.Skills.Select(s => new SkillLevel(s.DefName, s.Level))), row.Biography.WorkSpeeds));
        }

        var craft = StockArrivalRules.Craft(catalog.StockRecipes(finished), benches, crafters, stock);
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
                Findings = EquatableArray.From(craft.Findings.Concat(findings).Distinct().Order(StringComparer.Ordinal)),
                Medicines = EquatableArray.From(medicines),
            };
    }

    // The trader-visit sources (#3869): the storyteller's decoded trader comps, the trader kinds' stock, the visit facts the native
    // read carries, the colony's wealth and raid points, and the silver above the routine trade's reserve (#3855 Q2 A, #3907): the map's
    // for caravans and visitors, the silver within trade-beacon range for orbital traders (#3910: the routine trade's reserve holds
    // against the sheet's silver, which for an orbital trader is the beacon's).
    private static (IReadOnlyList<TradeSource> Sources, IReadOnlyList<string> Findings, string Unread) Trade(DefinitionCatalog catalog, O.ThreatFacts threat,
        long silver, long colonists)
    {
        if (threat.Trade is not { } facts)
        {
            return ([], [], "stock_arrival_trade_facts");
        }

        var schedule = catalog.TradeSchedule();
        if (!schedule.Storytellers.TryGetValue(threat.Storyteller, out var storyteller))
        {
            return ([], [], "storyteller_unknown:" + threat.Storyteller);
        }

        return TradeArrivalRules.Sources(schedule, storyteller, catalog.TraderStock(), facts, threat.RaidPoints, threat.WealthTotal,
            TradePolicyRules.SpendableSilver(silver, colonists), TradePolicyRules.SpendableSilver(facts.BeaconSilver, colonists));
    }
}
