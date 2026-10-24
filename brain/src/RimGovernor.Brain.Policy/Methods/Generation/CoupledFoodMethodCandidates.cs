using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal sealed record CoupledFoodCandidates(ForecastActivityOption[] Options,
    ForecastActivityRoute[] Routes, ForecastNeedTask[] Needs, ForecastCommitment[] Alternatives,
    IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings, bool Truncated)
{
    public EquatableArray<ForecastCompositionOperation> Operations { get; init; }
    public EquatableArray<ForecastCompositionSupply> Observed { get; init; }
    public EquatableArray<string> Findings { get; init; }
}

internal static class FoodMethodCandidates
{
    /// <summary>Vanilla JobDriver_Ingest reserves a food stack with maxPawns 10 and the eaten count; a generated delivery stack shares it.</summary>
    private const int IngestMaxPawns = 10;

    internal static CoupledFoodCandidates GenerateCoupled(CoupledForecastRead read, NeedsForecastRead needs, long deadline)
    {
        var tasks = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new Dictionary<(string, string, string), ForecastActivityRoute>();
        var needTasks = new List<ForecastNeedTask>(); var alternatives = new List<ForecastCommitment>();
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        var truncated = false; var expanded = 0;
        var horizon = (double)(deadline - read.Tick);
        var compositionNeeds = new Dictionary<string, ForecastCompositionRequirement[]>(StringComparer.Ordinal);
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        string Location(Cell c) => ForecastLocations.Of(c);
        bool Space(int count) { if (++expanded > 128 || tasks.Count + count > 128) { truncated = true; return false; } return true; }
        void Route(string pawn, string from, string to, double ticks) => routes.TryAdd((pawn, from, to), new(pawn, from, to, new RouteTicks.Reachable(ticks)));
        void Option(string id, string pawn, double ticks)
            => options.Add(new(id, pawn, Known(ticks), Known(0.0)) { Equipment = [] });
        void Outcome(string id, params string[] completion)
            => alternatives.Add(new(id, []) { ActivityCompletions = EquatableArray.From(completion), ExclusiveOutcome = id });
        // A produced lot keeps as long as its shortest-keeping observed use; with no use of the resource its shelf life is unread.
        Fact<double> Fresh(string resource)
        {
            var estimates = read.FoodUses.Where(u => u.Resource == resource).Select(u => Keeps(u.FreshTicks)).ToArray();
            return estimates.Length != 0 ? Known(estimates.Min()) : default;
        }
        var harvests = new Dictionary<string, string>(StringComparer.Ordinal);
        // Seed each source once. The full observed yield is physical output, regardless of one carrier's load.
        foreach (var group in read.Food.Options.GroupBy(o => o.Target).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!Space(1)) break;
            var row = group.First(); var id = "harvest/" + group.Key;
            var yield = Known((double)row.SourceCount);
            tasks.Add(new(id, Location(row.Origin), Location(row.Origin), []) { Outputs = [new(new(row.Resource), yield) { Lot = id, Perishable = true, FreshTicks = Fresh(row.Resource) }] });
            harvests[group.Key] = id;
            foreach (var pawn in group.GroupBy(o => o.Pawn).Select(g => g.First()))
            {
                Route(pawn.Pawn, "start", Location(pawn.Origin), pawn.ApproachTicks);
                if (pawn.HarvestAllowed)
                {
                    options.Add(new(id, pawn.Pawn, Known(pawn.WorkTicks), Known(0d)) { SkillWork = pawn.SkillWork });
                    bindings[(id, pawn.Pawn)] = new(id, pawn.Pawn, [], PawnActivityKind.Work, pawn.Target, pawn.WorkGiver, "Harvest", 0, deadline, pawn.Origin);
                }
            }
        }
        // Domains expose legal recipe/ingredient families; composition chooses the actual sources and quantities.
        // Different ingredient resources with unequal native recipe conversion are not silently interchangeable.
        var conversions = read.FoodConversions.Where(c => c.IngredientCount > 0)
            .GroupBy(c => (c.Bench, c.Bill, c.Recipe, c.Ingredient, c.IngredientCount, c.Destination))
            .OrderBy(g => g.Key.Bench, StringComparer.Ordinal).ThenBy(g => g.Key.Recipe, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Ingredient, StringComparer.Ordinal).ToArray();
        int Available(IEnumerable<CoupledFoodConversion> group) => group.GroupBy(c => c.Source).Sum(g => g.First().AvailableInput);
        if (conversions.Any(g => Available(g) / g.Key.IngredientCount > 32)) truncated = true;
        foreach (var candidate in conversions.SelectMany(g => Enumerable.Range(1, Math.Min(32, Available(g) / g.Key.IngredientCount))
            .Select(batch => (Group: g, Batch: batch))).OrderBy(c => c.Batch)
            .ThenBy(c => c.Group.Key.Bench, StringComparer.Ordinal).ThenBy(c => c.Group.Key.Recipe, StringComparer.Ordinal))
        {
            var group = candidate.Group; var first = group.First(); var batch = candidate.Batch;
            if (!Space(2 * batch)) break;
            var id = "food/" + alternatives.Count;
            var deliveries = new List<string>(); string? previous = null;
            for (var iteration = 0; iteration < batch; iteration++)
            {
                var cook = id + "/recipe/" + iteration; var deliver = id + "/delivery/" + iteration;
                var eligible = new List<string>();
                foreach (var source in group.GroupBy(c => c.Source).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var native = source.First();
                    if (native.RequiresHarvest && !harvests.ContainsKey(native.Source)) continue;
                    var lot = native.RequiresHarvest ? harvests[native.Source] : native.Source;
                    var quantityLimit = Math.Min(native.AvailableInput, native.IngredientCount);
                    for (var quantity = quantityLimit; quantity >= 1; quantity--)
                    {
                        if (!Space(1)) break;
                        var supply = id + "/supply/" + iteration + "/" + native.Source + "/" + quantity;
                        tasks.Add(new(supply, Location(native.SourceCell), Location(native.BenchCell), [])
                        {
                            Inputs = [new(new(native.Ingredient), Known((double)quantity)) { Lot = lot }],
                            Outputs = [new(new(native.Ingredient), Known((double)quantity)) { Lot = lot }],
                            ChoiceIdentity = id + "/supply-choice/" + iteration + "/" + native.Source,
                        });
                        compositionNeeds[supply] = [new("source", new(native.Ingredient), Known(quantity), Location(native.SourceCell),
                            ForecastRequirementKind.Consumed, Known<EquatableArray<string>>([native.RequiresHarvest
                                ? YieldId(harvests[native.Source], new(native.Ingredient)) : "observed/" + native.Source]), Known(0d), Known(horizon))];
                        eligible.Add(YieldId(supply, new(native.Ingredient)));
                        foreach (var row in source.GroupBy(c => c.Pawn).Select(g => g.First()))
                        {
                            Route(row.Pawn, "start", Location(row.SourceCell), row.ApproachTicks);
                            var trips = row.CarryingCapacity > 0 ? Math.Ceiling((double)quantity / row.CarryingCapacity) : 0;
                            // Several trips walk back from the bench between loads: an unreachable return leaves the pawn no supply option.
                            var back = trips > 1 ? read.Routes.FirstOrDefault(r => !r.FromStart && r.Pawn == row.Pawn && r.From == row.BenchCell && r.To == row.SourceCell) : null;
                            if (trips > 1 && back is null) findings.Add("unrouted:" + row.Pawn + ":" + Location(row.BenchCell) + "->" + Location(row.SourceCell));
                            double? ticks = trips == 1 ? row.SupplyTicks
                                : back?.Ticks is RouteTicks.Reachable(var walk) ? trips * row.SupplyTicks + (trips - 1) * walk : null;
                            if (row.CanSupply && row.CarryingCapacity > 0 && ticks is { } carry) Option(supply, row.Pawn, carry);
                            bindings[(supply, row.Pawn)] = new(supply, row.Pawn, [], PawnActivityKind.Deliver,
                                row.RequiresHarvest ? "observed-output" : row.Source, "", "", quantity, deadline, row.SourceCell)
                            { Resource = row.Ingredient, Source = row.SourceCell, Destination = row.BenchCell, Capacity = row.CarryingCapacity };
                        }
                    }
                }
                compositionNeeds[cook] = [new("ingredients", new(first.Ingredient), Known(first.IngredientCount), Location(first.BenchCell),
                    ForecastRequirementKind.Consumed, Known(EquatableArray.From(eligible)), Known(0d), Known(horizon))];
                var products = group.GroupBy(c => c.Product).Select(g => new ForecastActivityYield(new(g.Key), Known((double)g.First().ProductCount))
                    { Lot = cook, Perishable = true, FreshTicks = Fresh(g.Key) }).ToArray();
                tasks.Add(new(cook, Location(first.BenchCell), Location(first.BenchCell), previous is null ? [] : [previous])
                    { Outputs = EquatableArray.From(products), RequiresFoodUse = true });
                tasks.Add(new(deliver, Location(first.BenchCell), Location(first.Destination), [])
                    { Inputs = EquatableArray.From(products), Outputs = EquatableArray.From(products) });
                foreach (var row in group.GroupBy(c => c.Pawn).Select(g => g.First()))
                {
                    if (row.Cooking is FoodCooking.Cook cooking)
                    {
                        options.Add(new(cook, row.Pawn, Known(cooking.WorkTicks), Known(0d)) { Equipment = [row.Bench], SkillWork = cooking.SkillWork });
                        if (cooking.SkillWork is null) findings.Add("productive_learning_unassessed:" + row.Pawn + ":" + row.Recipe);
                        findings.Add("cooking_poisoning_and_future_environment_unassessed");
                    }
                    if (row.Delivery is FoodDelivery.Carried(var carried)) Option(deliver, row.Pawn, carried);
                    bindings[(cook, row.Pawn)] = new(cook, row.Pawn, [], PawnActivityKind.Recipe, row.Bench, row.WorkGiver, "", 0, deadline, row.BenchCell)
                    { Resource = row.Ingredient, Source = row.BenchCell, Recipe = new(row.Bill, row.Recipe, [new("observed-output", row.IngredientCount)]) { Resource = row.Ingredient },
                      FoodPoisonChance = row.Cooking is FoodCooking.Cook { FoodPoisonChance: var poison } ? Known(poison) : default };
                    bindings[(deliver, row.Pawn)] = new(deliver, row.Pawn, [], PawnActivityKind.Deliver, "observed-output", "", "", row.ProductCount, deadline, row.BenchCell)
                    { Resource = row.Product, Source = row.BenchCell, Destination = row.Destination };
                }
                compositionNeeds[deliver] = products.Select((product, index) => new ForecastCompositionRequirement("product/" + index,
                    product.Resource, Units(product.Units), Location(first.BenchCell), ForecastRequirementKind.Consumed,
                    Known<EquatableArray<string>>([YieldId(cook, product.Resource)]), Known(0d), Known(horizon))).ToArray();
                deliveries.Add(deliver); previous = cook;
                foreach (var product in products) AddEating(deliver, product.Resource, product.Units, first.Destination, cook, first.Ingredient);
            }
            Outcome(id, deliveries.ToArray());
        }
        foreach (var group in read.Food.Options.GroupBy(o => (o.Target, o.Destination))
            .OrderBy(g => g.Key.Target, StringComparer.Ordinal).ThenBy(g => g.Key.Destination.X).ThenBy(g => g.Key.Destination.Z))
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
            compositionNeeds[id] = [new("source", new(first.Resource), Known(count), Location(first.Origin), ForecastRequirementKind.Consumed,
                Known<EquatableArray<string>>([YieldId(harvest, new(first.Resource))]), Known(0d), Known(horizon))];
            Outcome(id, id); AddEating(id, new(first.Resource), Known((double)count), first.Destination, harvest);
        }
        // Typed inputs retain native-certified lot identity. Only the shared composer connects producers.
        string YieldId(string task, Resource resource) => task + "/yield/" + resource.Value;
        Fact<int> Units(Fact<double> quantity) => quantity.TryGetValue(out var n) && double.IsFinite(n)
            && n >= 0 && n <= int.MaxValue && n == Math.Floor(n) ? Known((int)n) : default;
        Fact<double> Expiry(Resource resource, string lot)
        {
            var physical = read.FoodSources.Where(s => s.Target == lot && s.Resource == resource.Value).ToArray();
            var producedHere = tasks.Any(t => t.Outputs.Any(o => o.Perishable && o.Resource == resource && o.Lot == lot));
            var estimate = physical.Length == 0 ? producedHere ? Fresh(resource.Value) : default
                : Known(physical.Min(s => Keeps(s.FreshTicks)));
            return estimate.TryGetValue(out var fresh) ? Known(Math.Min(horizon, fresh)) : default;
        }
        var observed = read.FoodSources.Select(s => new ForecastCompositionSupply("observed/" + s.Target, new(s.Resource), Known(s.Count),
                Location(s.Location), Known(0d), Known(Math.Min(horizon, Keeps(s.FreshTicks))))
                { Lot = s.Target, Provenance = "observed-food/" + s.Target })
            .Concat(read.FoodConversions.Where(c => !c.RequiresHarvest).Select(c => new ForecastCompositionSupply("observed/" + c.Source,
                new(c.Ingredient), Known(c.AvailableInput), Location(c.SourceCell), Known(0d), Expiry(new(c.Ingredient), c.Source))
                { Lot = c.Source, Provenance = "native-recipe-source/" + c.Source }))
            .GroupBy(s => s.Id).Select(g => g.Select(s => s.Units).Distinct().Count() == 1 ? g.First() : g.First() with { Units = default }).ToArray();
        var produced = tasks.SelectMany(t => t.Outputs.Select(o => new ForecastCompositionSupply(YieldId(t.Id, o.Resource), o.Resource,
            Units(o.Units), o.LotLocation.Length == 0 ? t.EndLocation : o.LotLocation, Known(0d), Expiry(o.Resource, o.Lot))
            { Producer = t.Id, Lot = o.Lot.Length == 0 ? t.Id : o.Lot, Provenance = "native-food-operation/" + t.Id })).ToArray();
        var all = observed.Concat(produced).ToArray();
        var operations = tasks.Select(t =>
        {
            var requirements = compositionNeeds.GetValueOrDefault(t.Id) ?? t.Inputs.Select((input, index) =>
                new ForecastCompositionRequirement("input/" + index, input.Resource, Units(input.Units),
                    input.LotLocation.Length == 0 ? t.Location : input.LotLocation, ForecastRequirementKind.Consumed,
                    Known(EquatableArray.From(all.Where(s => s.Producer != t.Id && s.Resource == input.Resource && s.Lot == input.Lot
                        && s.Location == (input.LotLocation.Length == 0 ? t.Location : input.LotLocation)).Select(s => s.Id))),
                    Known(t.NotBefore), Known(horizon))).ToArray();
            if (requirements.Any(r => !r.Units.IsKnown)) findings.Add("unsupported_fractional_food_quantity:" + t.Id);
            // Keep only semantic ordering dependencies; material links are reconstructed by the shared composer.
            var materialParents = requirements.SelectMany(r => r.Sources.Value).Select(id => all.Single(s => s.Id == id).Producer).ToHashSet();
            var operation = t with { Prerequisites = t.Prerequisites.Where(p => !materialParents.Contains(p)).ToArray() };
            return new ForecastCompositionOperation(operation, EquatableArray.From(requirements), EquatableArray.From(produced.Where(s => s.Producer == t.Id)))
                { ChoiceGroup = t.ChoiceIdentity };
        }).ToArray();
        return new(options.ToArray(), routes.Values.ToArray(), needTasks.ToArray(), alternatives.ToArray(), bindings, truncated)
            { Operations = EquatableArray.From(operations), Observed = EquatableArray.From(observed), Findings = EquatableArray.From(findings) };
        // A cooked batch is eaten with the thoughts of its own ingredient (#3872); anything else as it is.
        void AddEating(string supply, Resource resource, Fact<double> quantity, Cell cell, string freshFrom, string ingredient = "")
        {
            if (!quantity.TryGetValue(out var units)) return;
            foreach (var use in read.FoodUses.Where(u => u.Resource == resource.Value && u.Location == cell && u.Ingredient == ingredient)
                .OrderBy(u => u.Pawn, StringComparer.Ordinal))
            {
                var gain = use.Nutrition;
                if (!use.Eligible || gain <= 0) continue;
                var pawn = needs.Pawns.SingleOrDefault(p => p.Pawn == use.Pawn);
                var food = pawn?.Needs.SingleOrDefault(n => n.Kind == "food");
                if (food is null) continue;
                var serving = Math.Min((int)units, use.MaxCount > 0 ? use.MaxCount : (int)Math.Ceiling(1 / gain));
                // Each produced lot can serve an early or later demand event. Pinning every single-meal
                // recipe to event zero hid productive multi-session allocations and forced overfeeding.
                // Shared stock conservation limits how many choices can consume this physical lot.
                for (var meal = 0; meal < 3; meal++)
                {
                    if (!Space(1)) break;
                    var count = Math.Min((int)units, serving); var id = supply + "/eat/" + use.Pawn + "/" + meal;
                    var earliest = food.FallPerTick > 0 ? Math.Max(0,
                        (food.Level - food.Threshold + meal * serving * gain) / food.FallPerTick - use.ChewTicks) : 0;
                    if (earliest + use.ChewTicks > horizon) continue;
                    tasks.Add(new(id, Location(cell), Location(cell), [supply])
                    { Inputs = [new(resource, Known((double)count)) { Lot = freshFrom }], NotBefore = earliest });
                    Option(id, use.Pawn, use.ChewTicks);
                    bindings[(id, use.Pawn)] = new(id, use.Pawn, [supply], PawnActivityKind.Eat, "observed-output", "", "", count, deadline, cell) { Resource = resource.Value, Source = cell };
                    needTasks.Add(new(id, use.Pawn, "food", Location(cell), resource, count, Known(count * gain), use.Thoughts)
                    { FoodReady = supply, FreshFrom = freshFrom, FreshTicks = Known(Keeps(use.FreshTicks)), AvailableCount = quantity, Batch = supply,
                        MaxPawns = Known(IngestMaxPawns) });
                    Outcome(id, id);
                }
            }
        }
    }

    /// <summary>The ticks a fresh unit keeps: <see cref="double.PositiveInfinity"/> for one that never rots.</summary>
    internal static double Keeps(FoodFreshness freshness) => freshness is FoodFreshness.Rots(var ticks) ? ticks : double.PositiveInfinity;
}
