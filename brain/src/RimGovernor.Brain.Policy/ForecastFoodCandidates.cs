namespace RimGovernor.Brain.Policy;

public sealed record ForecastFoodWorker(string Pawn, Fact<bool> HarvestAllowed, Fact<bool> HaulAllowed,
    Fact<double> HarvestWorkPerTick, Fact<double> CookWorkPerTick, Fact<double> RecoveryLaborTicks);
/// <summary>Per-resource carrying limit and conservative complete-trip cost; initial empty travel uses Routes.</summary>
public sealed record ForecastFoodTransfer(string Source, string Destination, string Pawn, Resource Resource,
    Fact<double> UnitsPerTrip, Fact<double> TicksPerTrip);
public sealed record ForecastFoodCandidateInputs
{
    public Tick Tick { get; init; }
    public Fact<EquatableArray<AcquisitionSource>> Sources { get; init; }
    public IReadOnlyList<CropChoice> Crops { get; init; } = [];
    public Fact<EquatableArray<ProductionBench>> Benches { get; init; }
    public Fact<EquatableArray<ProductionRecipeInput>> Recipes { get; init; }
    public ForecastFoodWorker[] Workers { get; init; } = [];
    public ForecastFoodTransfer[] Transfers { get; init; } = [];
    /// <summary>Conditional destination eligibility and absolute expiry, not a predicted eating job.</summary>
    public IReadOnlyDictionary<Resource, FoodStock> EdibleAtDestination { get; init; } = new Dictionary<Resource, FoodStock>();
    public string Destination { get; init; } = "";
    public int GenerationBudget { get; init; } = 16;
}
public sealed record ForecastFoodTaskBinding(string Activity, string Source, string Bench, string Recipe)
{
    /// <summary>Recipe iterations valued by this activity; not authorization for a repeating native job.</summary>
    public int Iterations { get; init; }
}
public sealed record ForecastFoodCandidates(ForecastActivityProblem? Problem, ForecastActivityRelief[] Relief,
    ForecastFoodTaskBinding[] Bindings, string[] Findings, int Generated, bool GenerationTruncated);

public static partial class PolicyFunctions
{
    /// <summary>Generate one observed plant's raw and single-resource recipe alternatives before joint valuation.
    /// No inventory or actions are written. The existing scheduler/effect evaluator owns selection.</summary>
    public static ForecastFoodCandidates GenerateFoodActivities(ColonyForecastRequest frame,
        ForecastActivityProblem capacity, ForecastFoodCandidateInputs input)
    {
        if (input.Tick.Value != frame.Origin.ObservedTick || input.GenerationBudget is < 1 or > 256
            || string.IsNullOrWhiteSpace(input.Destination)) throw new DomainException("invalid food candidate frame or budget");
        var needs = SurvivalNeedsOf(frame);
        var findings = new SortedSet<string>(needs.Findings, StringComparer.Ordinal);
        var activities = capacity.Activities.ToList();
        var options = capacity.Options.ToList();
        var relief = new List<ForecastActivityRelief>();
        var bindings = new List<ForecastFoodTaskBinding>();
        var generated = 0;
        var truncated = false;
        ForecastFoodCandidates Result() => new(relief.Count == 0 ? null : capacity with { Activities = activities.ToArray(), Options = options.ToArray() },
            relief.ToArray(), bindings.ToArray(), findings.ToArray(), generated, truncated);
        if (!input.Sources.TryGetValue(out var sources) || !frame.Demand.Work.Pawns.TryGetValue(out var roster))
        { findings.Add("unknown_food_sources_or_roster"); return Result(); }
        if (sources.Select(s => s.Id).Distinct().Count() != sources.Count
            || input.Workers.Select(w => w.Pawn).Distinct().Count() != input.Workers.Length
            || input.Workers.Any(w => !roster.Any(p => p.Id.Value == w.Pawn) || !capacity.Pawns.Any(p => p.Pawn == w.Pawn)))
            throw new DomainException("invalid food source or worker identities");
        var recipes = input.Recipes.TryGetValue(out var catalog) ? catalog.ToDictionary(r => r.Recipe, StringComparer.Ordinal) : [];
        if (!input.Benches.IsKnown || !input.Recipes.IsKnown) findings.Add("unknown_food_kitchen");
        var workers = input.Workers.Where(w =>
        {
            var pawn = roster.Single(p => p.Id.Value == w.Pawn);
            if (!pawn.Available.TryGetValue(out var available))
            { findings.Add("unknown_food_worker_availability:" + w.Pawn); return false; }
            if (!w.HarvestAllowed.IsKnown || !w.HaulAllowed.IsKnown) findings.Add("unknown_food_worker_eligibility:" + w.Pawn);
            if (!pawn.Skills.IsKnown) findings.Add("unknown_food_worker_skills:" + w.Pawn);
            return available;
        }).ToArray();
        bool Spend()
        {
            if (generated == input.GenerationBudget) { truncated = true; findings.Add("food_generation_truncated"); return false; }
            generated++; return true;
        }
        Fact<double> Duration(Fact<double> work, Fact<double> rate)
        {
            if (!work.TryGetValue(out var amount) || !rate.TryGetValue(out var speed)) return default;
            if (!double.IsFinite(amount) || amount < 0 || !double.IsFinite(speed) || speed < 0)
                throw new DomainException("invalid food work estimate");
            return speed > 0 ? Known(amount / speed) : default;
        }
        void Transfer(string id, string prior, string from, string to, Resource resource, double quantity)
        {
            activities.Add(new(id, from, to, [prior]) { Inputs = EquatableArray.From(new[] { new ForecastActivityYield(resource, Known(quantity)) }),
                Outputs = EquatableArray.From(new[] { new ForecastActivityYield(resource, Known(quantity)) }) });
            foreach (var worker in workers.Where(w => w.HaulAllowed.TryGetValue(out var allowed) && allowed))
            {
                var transfer = input.Transfers.SingleOrDefault(t => t.Source == from && t.Destination == to && t.Pawn == worker.Pawn && t.Resource == resource);
                Fact<double> duration = default;
                if (transfer is not null && transfer.UnitsPerTrip.TryGetValue(out var limit) && transfer.TicksPerTrip.TryGetValue(out var ticks))
                {
                    if (!double.IsFinite(limit) || limit <= 0 || !double.IsFinite(ticks) || ticks < 0) throw new DomainException("invalid food transfer estimate");
                    duration = Known(Math.Ceiling(quantity / limit) * ticks);
                }
                options.Add(new(id, worker.Pawn, duration, worker.RecoveryLaborTicks));
            }
        }
        void Relief(string terminal, Resource product)
        {
            if (!input.EdibleAtDestination.TryGetValue(product, out var evidence))
            { findings.Add("unknown_candidate_edibility:" + product.Value); return; }
            foreach (var need in needs.Needs.Where(n => n.Basis == "eligible_food_exhaustion" && n.Window.TryGetValue(out var window) && window.Due))
            {
                if (relief.Count == 16) { truncated = true; findings.Add("food_generation_truncated"); return; }
                relief.Add(new(terminal + "/" + need.Pawn.Value, need.Id, terminal) { Food = new(product, evidence) });
            }
        }
        foreach (var source in sources.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (!Spend()) break;
            if (!source.Food || source.Tree || source.Hunt || source.Taken || source.Designated) continue;
            var crop = input.Crops.SingleOrDefault(c => c.Name == source.Definition);
            if (crop is null || !crop.Harvests.TryGetValue(out var raw) || raw.Value != source.Resource
                || !crop.HarvestMinGrowth.TryGetValue(out var minimum) || !crop.HarvestWork.IsKnown)
            { findings.Add("unknown_harvest_catalog:" + source.Id); continue; }
            if (!double.IsFinite(source.Yield) || source.Yield <= 0 || !double.IsFinite(source.Growth)
                || !double.IsFinite(minimum) || minimum < 0 || minimum > 1 || source.Growth < minimum) continue;
            if (activities.Count + 2 > 32) { truncated = true; findings.Add("food_generation_truncated"); break; }
            var harvest = "food-" + generated + "-harvest";
            activities.Add(new(harvest, source.Id, source.Id, []) { Outputs = EquatableArray.From(new[] { new ForecastActivityYield(raw, Known(source.Yield)) }) });
            bindings.Add(new(harvest, source.Id, "", ""));
            foreach (var worker in workers.Where(w => w.HarvestAllowed.TryGetValue(out var allowed) && allowed))
                options.Add(new(harvest, worker.Pawn, Duration(crop.HarvestWork, worker.HarvestWorkPerTick), worker.RecoveryLaborTicks));
            var rawDelivery = harvest + "-raw";
            Transfer(rawDelivery, harvest, source.Id, input.Destination, raw, source.Yield);
            Relief(rawDelivery, raw);
            foreach (var bench in input.Benches.ValueOrDefault.OrderBy(b => b.Id, StringComparer.Ordinal))
            foreach (var recipe in bench.Recipes.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                if (truncated || !Spend()) break;
                if (!bench.Usable.TryGetValue(out var usable))
                { findings.Add("unknown_food_bench:" + bench.Id); continue; }
                if (!usable || recipe.Role != RecipeRole.OrdinaryMeal) continue;
                if (!recipe.Available.TryGetValue(out var allowed))
                { findings.Add("unknown_food_recipe_availability:" + recipe.Name); continue; }
                if (!allowed) continue;
                if (!recipes.TryGetValue(recipe.Name, out var exact) || !exact.Slots.TryGetValue(out var slots)
                    || !exact.Products.TryGetValue(out var products) || products.Count != 1 || products[0].Count <= 0
                    || !exact.WorkAmount.IsKnown || !exact.UsesUnfinishedItem.TryGetValue(out var unfinished) || unfinished
                    || !recipe.CookSkillFloor.TryGetValue(out var floor))
                { findings.Add("unsupported_or_unknown_food_recipe:" + recipe.Name); continue; }
                // Narrow slice: every required slot must admit the observed harvest resource.
                var amounts = slots.Select(s => s.SingleOrDefault(a => a.Resource == raw)).ToArray();
                if (slots.Count == 0 || amounts.Any(a => a is null || a.Count <= 0)) continue;
                var perBatch = amounts.Sum(a => (double)a!.Count);
                var maximumBatches = Math.Floor(source.Yield / perBatch);
                // Small batches can cover the need before a full harvest's worth of cooking would finish.
                // Each alternative spends the same bounded generation budget; omitted sizes stay explicit.
                for (var batches = 1; batches <= maximumBatches; batches++)
                {
                    if (!Spend()) break;
                    if (activities.Count + 3 > 32) { truncated = true; findings.Add("food_generation_truncated"); break; }
                    var id = harvest + "-recipe-" + generated;
                    Transfer(id + "-supply", harvest, source.Id, bench.Id, raw, perBatch * batches);
                    var cook = id + "-cook";
                    activities.Add(Methods.Generation.FoodRecipeActivity.Create(cook, bench.Id, [id + "-supply"],
                        raw, perBatch * batches, products[0].Resource, (double)products[0].Count * batches));
                    bindings.Add(new(cook, source.Id, bench.Id, recipe.Name) { Iterations = batches });
                    foreach (var worker in workers)
                    {
                        var pawn = roster.Single(p => p.Id.Value == worker.Pawn);
                        if (!pawn.Available.TryGetValue(out var available) || !available || !pawn.Skills.TryGetValue(out var skills)
                            || !skills.Any(s => s.Name == "Cooking" && !s.Disabled && s.Level >= floor)) continue;
                        options.Add(new(cook, worker.Pawn, Duration(Known(exact.WorkAmount.Value * batches), worker.CookWorkPerTick), worker.RecoveryLaborTicks)
                            { Equipment = EquatableArray.From(new[] { bench.Id }) });
                    }
                    var delivery = id + "-delivery";
                    Transfer(delivery, cook, bench.Id, input.Destination, products[0].Resource, (double)products[0].Count * batches);
                    Relief(delivery, products[0].Resource);
                    if (truncated) break;
                }
            }
            if (truncated) break;
        }
        return Result();
    }
}
