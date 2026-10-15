using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal static class ClothingMethodCandidates
{
    internal static CoordinatedMethodCandidates Generate(RecipeActivityRead read,
        EquatableArray<ClothingActivityRequirement> requirements, long deadline, WorkResourceView resources)
    {
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick || resources.Claims.Truncated)
            return new(null, [], bindings, read.Truncated || resources.Claims.Truncated);
        var horizon = deadline - tick;
        var activities = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var outcomes = new List<ForecastActivityOutcome>(); var routes = new List<ForecastActivityRoute>();
        var workers = new HashSet<string>(StringComparer.Ordinal);
        var findings = new HashSet<string>(StringComparer.Ordinal);
        var garmentNeeds = new Dictionary<string, ClothingActivityRequirement>(StringComparer.Ordinal);
        var truncated = read.Truncated;
        foreach (var retained in read.Retained.OrderBy(r => r.Id, StringComparer.Ordinal))
            foreach (var need in requirements.Where(n => n.Product == retained.Product && n.Stuff == retained.Stuff))
            {
                if (outcomes.Count == 16) { truncated = true; break; }
                if (!retained.CompletionTicks.IsKnown) { findings.Add("unknown_retained_completion:" + retained.Id); continue; }
                var id = "retain-" + outcomes.Count;
                garmentNeeds[id] = need;
                outcomes.Add(new(id, "clothing/" + need.Wearer + "/" + need.Product, "", Known((double)horizon), default)
                { Requirements = need.Assessment, RetainedCompletion = retained.CompletionTicks, ExclusiveOutput = retained.Id });
            }
        var allocations = new List<RecipeActivityOption>();
        foreach (var row in read.Options.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var variants = AllocateInputs(row, out var bounded);
            truncated |= bounded;
            allocations.AddRange(variants);
        }
        var alternatives = requirements.SelectMany(need => allocations.Where(o => o.Product == need.Product && o.Stuff == need.Stuff)
            .OrderBy(o => o.Unfinished.Length == 0).ThenBy(o => o.Id, StringComparer.Ordinal).Select((row, index) => (need, row, index)))
            .OrderBy(x => x.index).ThenBy(x => x.need.Wearer, StringComparer.Ordinal).ThenBy(x => x.need.Product, StringComparer.Ordinal).ThenBy(x => x.need.Stuff, StringComparer.Ordinal);
        foreach (var (need, row, _) in alternatives)
        {
            if (resources.Claims.Pawns.Contains(row.Pawn) || resources.Claims.Targets.Contains(row.Bench)
                || resources.Claims.Targets.Contains("cell/" + row.Destination.X + "/" + row.Destination.Z)
                || row.Inputs.Any(i => resources.Claims.Targets.Contains(i.Thing))
                || row.Unfinished.Length != 0 && resources.Claims.Targets.Contains(row.Unfinished)) continue;
            if (outcomes.Count == 16) { truncated = true; break; }
            if (!row.TravelTicks.TryGetValue(out var travel) || !row.WorkTicks.TryGetValue(out var work)
                || !row.OutputTravelTicks.IsKnown) { findings.Add("unknown_recipe_estimate:" + row.Id); continue; }
            var id = "clothing-" + outcomes.Count;
            garmentNeeds[id] = need;
            var make = id + "-recipe";
            var origin = row.Inputs.Count > 0 ? row.Inputs[0].Location : row.Location;
            var approach = row.Inputs.Count > 0 ? row.Inputs[0].ApproachTicks : row.TravelTicks;
            var sourceLocation = ForecastLocations.Of(origin);
            var benchLocation = ForecastLocations.Of(row.Location);
            activities.Add(new(make, sourceLocation, benchLocation, [])
            {
                EquipmentLocation = benchLocation,
                Inputs = EquatableArray.From(row.Inputs.Select(i => new ForecastActivityYield(new(i.Resource), Known((double)i.Count)) { Lot = i.Thing, LotLocation = ForecastLocations.Of(i.Location) })),
                Outputs = [new(new(row.Product), Known(1.0)) { Lot = make }]
            });
            activities.Add(new(id, benchLocation, ForecastLocations.Of(row.Destination), [make])
            { Inputs = [new(new(row.Product), Known(1.0)) { Lot = make }], Outputs = [new(new(row.Product), Known(1.0)) { Lot = make }] });
            options.Add(new(make, row.Pawn, approach.IsKnown ? Known(travel - approach.Value + work) : default, Known(0.0))
            { Equipment = [row.Bench] });
            options.Add(new(id, row.Pawn, row.OutputTravelTicks, Known(0.0)));
            routes.Add(new(row.Pawn, "start", sourceLocation, approach));
            workers.Add(row.Pawn);
            bindings[(make, row.Pawn)] = new(make, row.Pawn, [], PawnActivityKind.Recipe, row.Bench, row.WorkGiver, "", 0, deadline, row.Location)
            {
                Resource = row.Stuff,
                Source = row.Location,
                Clothing = need,
                Inputs = EquatableArray.From(row.Inputs.Select(i => (i.Thing, i.Resource, i.Count, i.AvailableCount))),
                Recipe = new(row.Bill, row.Recipe, EquatableArray.From(row.Inputs.Select(i => new PawnActivityIngredient(i.Thing, i.Count))))
                { Resource = row.Stuff, Stuff = row.Stuff, Unfinished = row.Unfinished }
            };
            bindings[(id, row.Pawn)] = new(id, row.Pawn, [make], PawnActivityKind.Deliver, "observed-output", "", "", 1, deadline, row.Location)
            { Resource = row.Product, ProductStuff = row.Stuff, Source = row.Location, Destination = row.Destination };
            outcomes.Add(new(id, "clothing/" + need.Wearer + "/" + need.Product, id, Known((double)horizon), default)
            { ChoiceIdentity = "clothing/" + need.Wearer + "/" + need.Product, Requirements = need.Assessment with { Work = Known(travel + work + row.OutputTravelTicks.Value) } });
        }
        var capacities = workers.Order(StringComparer.Ordinal).Select(p => new ForecastPawnCapacity(p, "start",
            resources.Capacity.TryGetValue(p, out var capacity) && capacity.Capacity.Windows.TryGetValue(out var windows)
                ? Known(EquatableArray.From(windows.Select(w => w with
                {
                    Start = Math.Max(0, w.Start - (tick - resources.Evidence.Tick.Value)),
                    End = w.End - (tick - resources.Evidence.Tick.Value)
                }).Where(w => w.End > w.Start))) : default, Known(1.0))).ToArray();
        var alternativesWithConflicts = outcomes.Select(o => o with
        {
            Excludes = EquatableArray.From(garmentNeeds
            .Where(n => n.Key != o.Id && (n.Value.Wearer == garmentNeeds[o.Id].Wearer
                && n.Value.Layers.Intersect(garmentNeeds[o.Id].Layers).Any() && n.Value.Groups.Intersect(garmentNeeds[o.Id].Groups).Any()
                // A selected output occupies its exact destination beyond the delivery job.
                // Different garments cannot both claim that cell without an observed onward move.
                || bindings.Values.Any(a => a.Id == o.Id && a.Destination is not null
                    && bindings.Values.Any(b => b.Id == n.Key && b.Destination == a.Destination))))
                .Select(n => n.Key))
        }).ToArray();
        return new(new()
        {
            Activities = activities.ToArray(),
            Options = options.ToArray(),
            Routes = ForecastLocations.Merge(routes),
            Equipment = options.SelectMany(o => o.Equipment).Distinct().Select(id => new ForecastEquipmentCapacity(id, activities.Single(t => t.Id == options.First(o => o.Equipment.Contains(id)).Activity).EquipmentLocation, Known<EquatableArray<ForecastTimeWindow>>([new(0, horizon)]))).ToArray(),
            Pawns = capacities,
            Deadline = Known((double)horizon),
            AlternativeLaborCost = Known((double)horizon)
        }, alternativesWithConflicts, bindings, truncated)
        { Findings = EquatableArray.From(findings.Order(StringComparer.Ordinal)) };
    }

    private static RecipeActivityOption[] AllocateInputs(RecipeActivityOption row, out bool truncated)
    {
        truncated = false;
        if (row.Unfinished.Length != 0) return [row];
        var results = new List<RecipeActivityOption>();
        var chosen = new List<RecipeIngredientObservation>();
        var budget = 128; var exhausted = false;
        var inputs = row.Inputs.OrderBy(i => i.Thing, StringComparer.Ordinal).ToArray();
        // Seed every observed starting stack before refining partial splits, so one large far stack's
        // many integer allocations cannot consume the whole budget before nearby combinations appear.
        for (var start = 0; start < inputs.Length && results.Count < 16; start++)
        {
            var remaining = row.RequiredCount; double travel = 0;
            var seed = new List<RecipeIngredientObservation>();
            foreach (var input in inputs.Skip(start).Concat(inputs.Take(start)))
            {
                if (remaining == 0) break;
                if (input.Capacity <= 0 || !input.ApproachTicks.IsKnown || !input.CarryTicks.IsKnown || !input.ReturnTicks.IsKnown) continue;
                var take = Math.Min(remaining, input.AvailableCount);
                var trips = (take + input.Capacity - 1) / input.Capacity;
                travel += (seed.Count == 0 ? input.ApproachTicks.Value : input.ReturnTicks.Value)
                    + trips * input.CarryTicks.Value + (trips - 1) * input.ReturnTicks.Value;
                seed.Add(input with { Count = take }); remaining -= take;
            }
            if (remaining == 0 && seed.Count is > 0 and <= 16) results.Add(row with { Inputs = EquatableArray.From(seed), TravelTicks = Known(travel) });
        }
        void Search(int left, double travel)
        {
            if (left == 0) { results.Add(row with { Inputs = EquatableArray.From(chosen), TravelTicks = Known(travel) }); return; }
            if (chosen.Count == 16) { exhausted = true; return; }
            foreach (var input in inputs)
            {
                if (chosen.Any(c => c.Thing == input.Thing) || input.Capacity <= 0 || !input.ApproachTicks.IsKnown
                    || !input.CarryTicks.IsKnown || !input.ReturnTicks.IsKnown) continue;
                for (var take = Math.Min(left, input.AvailableCount); take > 0; take--)
                {
                    if (--budget < 0 || results.Count == 16) { exhausted = true; return; }
                    var trips = (take + input.Capacity - 1) / input.Capacity;
                    var approach = chosen.Count == 0 ? input.ApproachTicks.Value : input.ReturnTicks.Value;
                    chosen.Add(input with { Count = take });
                    Search(left - take, travel + approach + trips * input.CarryTicks.Value + (trips - 1) * input.ReturnTicks.Value);
                    chosen.RemoveAt(chosen.Count - 1);
                    if (exhausted) return;
                }
            }
        }
        if (row.RequiredCount > 0) Search(row.RequiredCount, 0);
        truncated = exhausted;
        return results.ToArray();
    }

}
