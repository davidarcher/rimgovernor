namespace RimGovernor.Brain.Observation;

/// <summary>
/// The stock-arrival model's read boundary (#3868): the catalog's stock recipes with the research finished, the bills census's
/// benches, the colonists who may craft and the stock on hand, in plain values, built once a review into
/// <see cref="StockArrival"/> (<see cref="StockArrivalRules.Craft"/>). A census the frame did not carry leaves the model unread,
/// named in <see cref="StockArrival.Unread"/>.
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
        foreach (var row in (pawns?.Pawns ?? []).Where(r => r.Colonist && !r.Dead && !r.Downed && !r.Drafted && r.MentalState is null && r.Settings.WorkApplies == true))
        {
            if (PawnRules.WorkHours([.. row.Settings.Schedule.Select(slot => slot.AssignmentDefName)]) is not { } hours)
            {
                return new StockArrival { Unread = "stock_arrival_schedule:" + row.Pawn.Id };
            }

            var works = row.Settings.Work.Where(w => w.Priority > 0 && !w.Disabled)
                .Select(w => ColonyDecodeReads.WireParseOpen(w.DefName, WorkType.Unrecognized)).Where(w => w != WorkType.Unrecognized);
            crafters.Add(new StockCrafter(row.Pawn.Id ?? "", hours, EquatableArray.From(works),
                EquatableArray.From(row.Biography.Skills.Select(s => new SkillLevel(s.DefName, s.Level))), row.Biography.WorkSpeeds));
        }

        return StockArrivalRules.Craft(catalog.StockRecipes(finished), benches, crafters, stock) with { Medicines = EquatableArray.From(medicines) };
    }
}
