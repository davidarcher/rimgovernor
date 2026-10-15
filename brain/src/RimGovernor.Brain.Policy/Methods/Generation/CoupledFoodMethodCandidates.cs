using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal sealed record CoupledFoodCandidates(ForecastActivity[] Tasks, ForecastActivityOption[] Options,
    ForecastActivityRoute[] Routes, ForecastNeedTask[] Needs, ForecastCommitment[] Alternatives,
    IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings, bool Truncated);

internal static partial class FoodMethodCandidates
{
    internal static CoupledFoodCandidates GenerateCoupled(CoupledForecastRead read, NeedsForecastRead needs, long deadline)
    {
        var tasks = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new Dictionary<(string, string, string), ForecastActivityRoute>();
        var needTasks = new List<ForecastNeedTask>(); var alternatives = new List<ForecastCommitment>();
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        var truncated = false; var expanded = 0;
        string Location(Cell c) => ForecastLocations.Of(c);
        bool Space(int count) { if (++expanded > 128 || tasks.Count + count > 128) { truncated = true; return false; } return true; }
        void Route(string pawn, string from, string to, Fact<double> ticks) => routes.TryAdd((pawn, from, to), new(pawn, from, to, ticks));
        void Option(string id, string pawn, Fact<double> ticks, string? equipment = null)
            => options.Add(new(id, pawn, ticks, Known(0.0)) { Equipment = equipment is null ? [] : [equipment] });
        void Outcome(string id, params string[] completion)
            => alternatives.Add(new(id, []) { ActivityCompletions = EquatableArray.From(completion), ExclusiveOutcome = id });
        Fact<double> Fresh(string resource)
        {
            var estimates = read.FoodUses.Where(u => u.Resource == resource).Select(u => u.FreshTicks).ToArray();
            return estimates.Length != 0 && estimates.All(t => t.IsKnown) ? Known(estimates.Min(t => t.Value)) : default;
        }
        var harvests = new Dictionary<string, string>(StringComparer.Ordinal);
        // Seed each source once. The full observed yield is physical output, regardless of one carrier's load.
        foreach (var group in read.Food.Options.Where(o => o.Cooking is null).GroupBy(o => o.Target).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!Space(1)) break;
            var row = group.First(); var id = "harvest/" + group.Key;
            var yield = row.SourceCount;
            tasks.Add(new(id, Location(row.Origin), Location(row.Origin), []) { Outputs = [new(new(row.Resource), yield) { Lot = id, Perishable = true, FreshTicks = Fresh(row.Resource) }] });
            harvests[group.Key] = id;
            foreach (var pawn in group.GroupBy(o => o.Pawn).Select(g => g.First()))
            {
                Route(pawn.Pawn, "start", Location(pawn.Origin), pawn.ApproachTicks);
                if (pawn.HarvestAllowed == Known(true))
                {
                    Option(id, pawn.Pawn, Known(pawn.WorkTicks));
                    bindings[(id, pawn.Pawn)] = new(id, pawn.Pawn, [], PawnActivityKind.Work, pawn.Target, pawn.WorkGiver, "Harvest", 0, deadline, pawn.Origin);
                }
            }
        }
        // Preserve batch sizes before any labor/need comparison. One conversion has all observed products.
        var conversions = read.FoodConversions.Where(c => c.IngredientCount > 0)
            .GroupBy(c => (c.Source, c.Bench, c.Bill, c.Recipe, c.Destination)).ToArray();
        if (conversions.Any(g => g.First().AvailableInput / g.First().IngredientCount > 32)) truncated = true;
        // Seed each recipe/source/destination before deeper batch refinements consume the bounded census.
        foreach (var candidate in conversions.SelectMany(g => Enumerable.Range(1, Math.Min(32, g.First().AvailableInput / g.First().IngredientCount))
            .Select(batch => (Group: g, Batch: batch))).OrderBy(c => c.Batch)
            .ThenBy(c => c.Group.Key.Source, StringComparer.Ordinal).ThenBy(c => c.Group.Key.Recipe, StringComparer.Ordinal))
        {
            var group = candidate.Group; var batch = candidate.Batch; var first = group.First();
            if (first.RequiresHarvest && !harvests.ContainsKey(group.Key.Source)) continue;
            var harvest = first.RequiresHarvest ? harvests[group.Key.Source] : group.Key.Source;
            var sourcePrerequisites = first.RequiresHarvest ? new[] { harvest } : [];
            {
                if (!Space(3 * batch)) break;
                var id = "food/" + alternatives.Count;
                var deliveries = new List<string>(); string? previous = null;
                for (var iteration = 0; iteration < batch; iteration++)
                {
                    var supply = id + "/supply/" + iteration; var cook = id + "/recipe/" + iteration; var deliver = id + "/delivery/" + iteration;
                    var count = first.IngredientCount;
                    tasks.Add(new(supply, Location(first.SourceCell), Location(first.BenchCell), sourcePrerequisites)
                    { Inputs = [new(new(first.Ingredient), Known((double)count)) { Lot = harvest }], Outputs = [new(new(first.Ingredient), Known((double)count)) { Lot = harvest }] });
                    var products = group.GroupBy(c => c.Product).Select(g => new ForecastActivityYield(new(g.Key), Known((double)g.First().ProductCount)) { Lot = cook, Perishable = true, FreshTicks = Fresh(g.Key) }).ToArray();
                    tasks.Add(new(cook, Location(first.BenchCell), Location(first.BenchCell), previous is null ? [supply] : [supply, previous])
                    { Inputs = [new(new(first.Ingredient), Known((double)count)) { Lot = harvest }], Outputs = EquatableArray.From(products) });
                    tasks.Add(new(deliver, Location(first.BenchCell), Location(first.Destination), [cook])
                    { Inputs = EquatableArray.From(products), Outputs = EquatableArray.From(products) });
                    foreach (var row in group.GroupBy(c => c.Pawn).Select(g => g.First()))
                    {
                        Route(row.Pawn, "start", Location(row.SourceCell), row.ApproachTicks);
                        if (row.CanCook) Option(cook, row.Pawn, row.WorkTicks, row.Bench);
                        if (row.CanDeliver) Option(deliver, row.Pawn, row.DeliveryTicks);
                        var trips = row.CarryingCapacity > 0 ? Math.Ceiling((double)count / row.CarryingCapacity) : 0;
                        var returnRoute = read.Routes.FirstOrDefault(r => !r.FromStart && r.Pawn == row.Pawn && r.From == row.BenchCell && r.To == row.SourceCell);
                        var ticks = trips == 0 ? default : trips == 1 ? row.SupplyTicks : row.SupplyTicks.IsKnown && returnRoute?.Ticks.IsKnown == true
                            ? Known(trips * row.SupplyTicks.Value + (trips - 1) * returnRoute.Ticks.Value) : default;
                        if (row.CanSupply && row.CarryingCapacity > 0) Option(supply, row.Pawn, ticks);
                        bindings[(supply, row.Pawn)] = new(supply, row.Pawn, EquatableArray.From(sourcePrerequisites), PawnActivityKind.Deliver, first.RequiresHarvest ? "observed-output" : first.Source, "", "", count, deadline, row.SourceCell)
                        { Resource = row.Ingredient, Source = row.SourceCell, Destination = row.BenchCell, Capacity = row.CarryingCapacity };
                        bindings[(cook, row.Pawn)] = new(cook, row.Pawn, previous is null ? [supply] : [supply, previous], PawnActivityKind.Recipe, row.Bench, row.WorkGiver, "", 0, deadline, row.BenchCell)
                        { Resource = row.Ingredient, Source = row.BenchCell, Recipe = new(row.Bill, row.Recipe, [new("observed-output", count)]) { Resource = row.Ingredient } };
                        bindings[(deliver, row.Pawn)] = new(deliver, row.Pawn, [cook], PawnActivityKind.Deliver, "observed-output", "", "", row.ProductCount, deadline, row.BenchCell)
                        { Resource = row.Product, Source = row.BenchCell, Destination = row.Destination };
                    }
                    deliveries.Add(deliver); previous = cook;
                    foreach (var product in products) AddEating(deliver, product.Resource, product.Units, first.Destination, cook);
                }
                Outcome(id, deliveries.ToArray());
            }
        }
        foreach (var group in read.Food.Options.Where(o => o.Cooking is null).GroupBy(o => (o.Target, o.Destination)))
        {
            if (!harvests.TryGetValue(group.Key.Target, out var harvest) || !Space(1)) continue;
            var first = group.First(); var id = "raw/" + alternatives.Count; var count = group.Max(o => o.Count);
            tasks.Add(new(id, Location(first.Origin), Location(first.Destination), [harvest])
            { Inputs = [new(new(first.Resource), Known((double)count)) { Lot = harvest }], Outputs = [new(new(first.Resource), Known((double)count)) { Lot = harvest }] });
            foreach (var row in group.GroupBy(o => o.Pawn).Select(g => g.First()).Where(o => o.Count >= count))
            {
                Option(id, row.Pawn, row.DeliveryTicks);
                bindings[(id, row.Pawn)] = new(id, row.Pawn, [harvest], PawnActivityKind.Deliver, "observed-output", "", "", count, deadline, row.Origin)
                { Resource = row.Resource, Source = row.Origin, Destination = row.Destination };
            }
            Outcome(id, id); AddEating(id, new(first.Resource), Known((double)count), first.Destination, harvest);
        }
        return new(tasks.ToArray(), options.ToArray(), routes.Values.ToArray(), needTasks.ToArray(), alternatives.ToArray(), bindings, truncated);
        void AddEating(string supply, Resource resource, Fact<double> quantity, Cell cell, string freshFrom)
        {
            if (!quantity.TryGetValue(out var units)) return;
            foreach (var use in read.FoodUses.Where(u => u.Resource == resource.Value && u.Location == cell).OrderBy(u => u.Pawn, StringComparer.Ordinal))
            {
                if (use.Eligible != Known(true) || !use.Nutrition.TryGetValue(out var gain) || gain <= 0 || !use.ChewTicks.IsKnown) continue;
                var pawn = needs.Pawns.SingleOrDefault(p => p.Pawn == use.Pawn);
                var food = pawn?.Needs.SingleOrDefault(n => n.Kind == "food");
                if (food is null || !food.Level.IsKnown || !food.FallPerTick.IsKnown || !food.Threshold.IsKnown) continue;
                var serving = Math.Min((int)units, use.MaxCount > 0 ? use.MaxCount : (int)Math.Ceiling(1 / gain));
                var previous = supply; var left = (int)units;
                // Recurring consumption remains a dependency chain, with quantities and food shared by all eaters.
                for (var meal = 0; meal < 3 && left > 0; meal++)
                {
                    if (!Space(1)) break;
                    var count = Math.Min(left, serving); var id = supply + "/eat/" + use.Pawn + "/" + meal;
                    var earliest = food.FallPerTick.Value > 0 ? Math.Max(0,
                        (food.Level.Value - food.Threshold.Value + meal * serving * gain) / food.FallPerTick.Value - use.ChewTicks.Value) : 0;
                    tasks.Add(new(id, Location(cell), Location(cell), [previous])
                    { Inputs = [new(resource, Known((double)count)) { Lot = freshFrom }], NotBefore = earliest });
                    Option(id, use.Pawn, use.ChewTicks);
                    bindings[(id, use.Pawn)] = new(id, use.Pawn, [previous], PawnActivityKind.Eat, "observed-output", "", "", count, deadline, cell) { Resource = resource.Value, Source = cell };
                    needTasks.Add(new(id, use.Pawn, "food", Location(cell), resource, count, Known(count * gain), [])
                    { FoodReady = supply, FreshFrom = freshFrom, FreshTicks = use.FreshTicks, AvailableCount = quantity, ThoughtsComplete = false });
                    Outcome(id, id); previous = id; left -= count;
                }
            }
        }
    }
}
