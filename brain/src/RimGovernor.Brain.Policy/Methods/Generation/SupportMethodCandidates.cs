using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal sealed record SupportMethodCandidates(ForecastCompositionOperation[] Operations,
    ForecastCompositionSupply[] Observed, ForecastCommitment[] Roots, ForecastActivityOption[] Options,
    ForecastActivityRoute[] Routes, IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings,
    EquatableArray<string> Findings, bool Truncated)
{
    /// <summary>Optional support changes the same physical producer's dependencies, not a second scheduler.</summary>
    internal static SupportMethodCandidates Generate(SupportActivityRead read,
        IEnumerable<ForecastCompositionOperation> existingOperations,
        IEnumerable<ForecastActivityOption> existingOptions,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> existingBindings,
        WorkResourceView resources, long tick, long deadline)
    {
        var operations = existingOperations.ToList(); var options = existingOptions.ToList();
        var bindings = existingBindings.ToDictionary(p => p.Key, p => p.Value);
        var observed = new List<ForecastCompositionSupply>(); var roots = new List<ForecastCommitment>();
        var routes = new List<ForecastActivityRoute>(); var findings = new SortedSet<string>(read.Findings, StringComparer.Ordinal);
        var horizon = (double)(deadline - tick); var truncated = false;
        if (!read.Known) findings.Add("support_observation_unknown");
        bool Space() { if (operations.Count < 384) return true; truncated = true; return false; }
        bool Free(string pawn, string target) => !resources.Claims.Pawns.Contains(pawn) && !resources.Claims.Targets.Contains(target);
        string Location(Cell cell) => ForecastLocations.Of(cell);
        string Key(string value) => Uri.EscapeDataString(value);
        void Worker(string id, SupportWorker worker, string demand)
        {
            if (!Free(worker.Pawn, worker.Target) || worker.Medicine.Length != 0 && resources.Claims.Targets.Contains(worker.Medicine)) return;
            options.Add(new(id, worker.Pawn, worker.WorkTicks, Known(0d)));
            routes.Add(new(worker.Pawn, "start", Location(worker.Location), worker.ApproachTicks));
            bindings[(id, worker.Pawn)] = new(id, worker.Pawn, [], PawnActivityKind.Work, worker.Target,
                worker.WorkGiver, worker.Job, 0, deadline, worker.Location) { SupportDemand = demand, Medicine = worker.Medicine, Recipe = worker.Recipe, Source = worker.MedicineLocation };
        }
        var cleanTasks = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var demand in read.Demands.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            var cleans = new List<string>();
            foreach (var filth in read.Filth.Where(f => f.Demand == demand.Id).OrderBy(f => f.Target, StringComparer.Ordinal))
            {
                var id = "support/clean/" + filth.Target;
                if (operations.All(o => o.Activity.Id != id))
                {
                    if (!Space()) break;
                    operations.Add(new(new(id, Location(filth.Location), Location(filth.Location), []), [],
                        [new(id + "/ready", new("clean/" + filth.Target), Known(1), Location(filth.Location), Known(0d), Known(horizon))
                        { Producer = id, Kind = ForecastRequirementKind.Reusable, Provenance = "observed-filth-removal" }]) { ChoiceGroup = id });
                    foreach (var worker in read.Workers.Where(w => w.Job == "Clean" && w.Target == filth.Target).GroupBy(w => w.Pawn).Select(g => g.First()))
                        Worker(id, worker, demand.Id);
                }
                cleans.Add(id);
            }
            cleanTasks[demand.Id] = cleans.ToArray();
            if (demand.Kind is not ("care" or "surgery")) continue;
            foreach (var service in read.Workers.Where(w => w.Demand == demand.Id && w.Job is "TendPatient" or "DoBill")
                .GroupBy(w => (w.Medicine, Recipe: w.Recipe?.Bill ?? "")))
            {
            var care = "support/care/" + Key(demand.Id) + "/" + Key(service.Key.Medicine + service.Key.Recipe);
            var choice = "support/care/" + Key(demand.Id);
            if (!Space()) break;
            var firstWorker = service.First();
            var requirements = ServiceInputs(firstWorker);
            operations.Add(new(new(care, Location(demand.Location), Location(demand.Location), []), requirements, []) { ChoiceGroup = choice });
            foreach (var worker in service) Worker(care, worker, demand.Id);
            roots.Add(new(care, []) { ActivityCompletions = [care], ExclusiveOutcome = choice });
            if (cleans.Count > 0 && Space())
            {
                var before = care + "/clean-first";
                operations.Add(new(new(before, Location(demand.Location), Location(demand.Location), cleans.ToArray()), requirements, []) { ChoiceGroup = choice });
                foreach (var worker in service) Worker(before, worker, demand.Id);
                roots.Add(new(before, []) { ActivityCompletions = [before], ExclusiveOutcome = choice });
                // Post-care cleanup is an explicit alternative. It receives no invented health benefit;
                // it can serve a later observed producer in this same room through shared clean tasks.
                var after = new List<string>();
                foreach (var clean in cleans)
                {
                    if (!Space()) break;
                    var original = operations.Single(o => o.Activity.Id == clean); var id = clean + "/after/" + care;
                    operations.Add(original with { Activity = original.Activity with { Id = id, Prerequisites = [care] },
                        Supplies = EquatableArray.From(original.Supplies.Select(s => s with { Id = id + "/ready", Producer = id })) });
                    foreach (var option in options.Where(o => o.Activity == clean).ToArray())
                    {
                        options.Add(option with { Activity = id });
                        if (bindings.TryGetValue((clean, option.Pawn), out var binding)) bindings[(id, option.Pawn)] = binding with { Id = id };
                    }
                    after.Add(id);
                }
                roots.Add(new(care + "/post-clean", []) { ActivityCompletions = EquatableArray.From(after.Append(care)), ExclusiveOutcome = choice });
            }
            }
        }
        // A clean and dirty realization emit the same physical lot. The composer's choice identity
        // prevents both producing it, while source whitelists explicitly propose either realization.
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var original in operations.ToArray())
        {
            var producer = bindings.Values.FirstOrDefault(b => b.Id == original.Activity.Id && b.Kind == PawnActivityKind.Recipe);
            if (producer is null) continue;
            var demand = read.Demands.FirstOrDefault(d => d.Kind == "bench" && d.Target == producer.Target);
            if (demand is null || !cleanTasks.TryGetValue(demand.Id, out var cleans) || cleans.Length == 0 || !Space()) continue;
            var id = original.Activity.Id + "/clean-first";
            var group = original.ChoiceGroup.Length == 0 ? original.Activity.Id : original.ChoiceGroup;
            operations[operations.IndexOf(original)] = original with { ChoiceGroup = group };
            var supplies = original.Supplies.Select(s => { var next = s.Id + "/clean-first"; aliases[s.Id] = next; return s with { Id = next, Producer = id }; }).ToArray();
            operations.Add(original with { Activity = original.Activity with { Id = id, Prerequisites = original.Activity.Prerequisites.Concat(cleans).Distinct().ToArray() },
                ChoiceGroup = group, Supplies = EquatableArray.From(supplies) });
            foreach (var option in options.Where(o => o.Activity == original.Activity.Id).ToArray())
            {
                options.Add(option with { Activity = id });
                if (bindings.TryGetValue((original.Activity.Id, option.Pawn), out var binding)) bindings[(id, option.Pawn)] = binding with { Id = id };
            }
        }
        operations = operations.Select(o => o with { Requirements = EquatableArray.From(o.Requirements.Select(r => r.Sources.TryGetValue(out var sources)
            ? r with { Sources = Known(EquatableArray.From(sources.SelectMany(s => aliases.TryGetValue(s, out var alias) ? new[] { s, alias } : [s]))) } : r)) }).ToList();
        // Cleanliness is a reusable physical condition. A later producer may share cleanup performed
        // after care; requiring the base clean ID directly would make that combination impossible.
        var cleanOperations = operations.Where(o => o.Supplies.Any(s => s.Kind == ForecastRequirementKind.Reusable)).ToArray();
        operations = operations.Select(o =>
        {
            var cleanDependencies = o.Activity.Prerequisites.Where(p => cleanOperations.Any(c => c.Activity.Id == p)).ToArray();
            if (cleanDependencies.Length == 0) return o;
            return o with { Activity = o.Activity with { Prerequisites = o.Activity.Prerequisites.Except(cleanDependencies).ToArray() },
                Requirements = EquatableArray.From(o.Requirements.Concat(cleanDependencies.Select(id =>
                {
                    var condition = cleanOperations.Single(c => c.Activity.Id == id).Supplies[0];
                    return new ForecastCompositionRequirement("clean/" + id, condition.Resource, Known(1), condition.Location,
                        ForecastRequirementKind.Reusable, Known(EquatableArray.From(cleanOperations.SelectMany(c => c.Supplies)
                            .Where(s => s.Resource == condition.Resource && s.Location == condition.Location).Select(s => s.Id))), Known(0d), Known(horizon));
                }))) };
        }).ToList();
        foreach (var group in read.Deliveries.Where(d => d.Required > 0 && d.Available > 0 && d.Capacity > 0)
            .GroupBy(d => (d.Demand, d.Target, d.Destination)).OrderBy(g => g.Key.Demand, StringComparer.Ordinal).ThenBy(g => g.Key.Target, StringComparer.Ordinal))
        {
            var first = group.First(); var max = Math.Min(first.Required, first.Available);
            // Quantity is a decision, not a full-stack threshold. Demand and carrying capacity bound it.
            foreach (var count in Enumerable.Range(1, Math.Min(max, 64)))
            {
                if (!Space()) break;
                var id = "support/delivery/" + Key(first.Demand) + "/" + first.Target + "/" + Location(first.Destination) + "/" + count;
                var source = "support/observed/" + first.Target;
                if (observed.All(s => s.Id != source)) observed.Add(new(source, new(first.Resource), Known(first.Available), Location(first.Source), Known(0d), Known(horizon))
                    { Lot = first.Target, Provenance = "native-demand-source/" + first.Demand });
                operations.Add(new(new(id, Location(first.Source), Location(first.Destination), [])
                { Outputs = [new(new(first.Resource), Known((double)count)) { Lot = first.Target }], ChoiceIdentity = "support/delivery/" + Key(first.Demand) + "/" + first.Target },
                    [new("source", new(first.Resource), Known(count), Location(first.Source), ForecastRequirementKind.Consumed, Known<EquatableArray<string>>([source]), Known(0d), Known(horizon))],
                    [new(id + "/yield", new(first.Resource), Known(count), Location(first.Destination), Known(0d), Known(horizon)) { Producer = id, Lot = first.Target, Provenance = "native-demand-delivery/" + first.Demand }]));
                foreach (var row in group.Where(d => d.Capacity >= count && Free(d.Pawn, d.Target)))
                {
                    options.Add(new(id, row.Pawn, row.DeliveryTicks, Known(0d)));
                    routes.Add(new(row.Pawn, "start", Location(row.Source), row.ApproachTicks));
                    bindings[(id, row.Pawn)] = new(id, row.Pawn, [], PawnActivityKind.Deliver, row.Target, "", "", count, deadline, row.Source)
                    { Resource = row.Resource, Source = row.Source, Destination = row.Destination, Capacity = row.Capacity, SupportDemand = row.Demand, Container = row.Container };
                }
                roots.Add(new(id, []) { ActivityCompletions = [id] });
            }
            if (max > 64) truncated = true;
        }
        return new(operations.ToArray(), observed.ToArray(), roots.ToArray(), options.ToArray(), routes.ToArray(), bindings,
            EquatableArray.From(findings), truncated);

        EquatableArray<ForecastCompositionRequirement> ServiceInputs(SupportWorker worker)
        {
            if (worker.Medicine.Length == 0) return [];
            var source = "support/observed/" + worker.Medicine;
            if (worker.MedicineLocation is not { } cell) { findings.Add("care_medicine_location_unknown:" + worker.Medicine); return [new("medicine", new(worker.MedicineResource), default, "unknown", ForecastRequirementKind.Consumed, default, Known(0d), Known(horizon))]; }
            if (observed.All(s => s.Id != source)) observed.Add(new(source, new(worker.MedicineResource), Known(worker.MedicineAvailable), Location(cell), Known(0d), Known(horizon))
            { Lot = worker.Medicine, Provenance = "native-selected-medicine" });
            return [new("medicine", new(worker.MedicineResource), Known(worker.MedicineCount), Location(cell), ForecastRequirementKind.Consumed,
                Known<EquatableArray<string>>([source]), Known(0d), Known(horizon))];
        }
    }
}
