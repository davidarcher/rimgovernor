using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal sealed record SupportMethodCandidates(ForecastCompositionOperation[] Operations,
    ForecastCompositionSupply[] Observed, ForecastCommitment[] Roots, ForecastActivityOption[] Options,
    ForecastActivityRoute[] Routes, IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings,
    EquatableArray<string> Findings, bool Truncated)
{
    /// <summary>Optional support work (care, surgery, bench deliveries) beside the existing producers, not a second scheduler.</summary>
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
        bool Space() { if (operations.Count < 384) return true; truncated = true; return false; }
        bool Free(string pawn, string target) => !resources.Claims.Pawns.Contains(pawn) && !resources.Claims.Targets.Contains(target);
        string Location(Cell cell) => ForecastLocations.Of(cell);
        string Key(string value) => Uri.EscapeDataString(value);
        void Worker(string id, SupportWorker worker, string demand)
        {
            if (!Free(worker.Pawn, worker.Target) || worker.Medicine is { } stack && resources.Claims.Targets.Contains(stack.Id)) return;
            var surgery = worker.Treatment as SupportTreatment.Surgery;
            // Real medical work learns (#3344): surgery through its recipe work, a tend once at completion.
            options.Add(new(id, worker.Pawn, Known(worker.WorkTicks), Known(0d))
                { SkillWork = surgery?.SkillWork, TendLearning = (worker.Treatment as SupportTreatment.Tend)?.Learning });
            routes.Add(new(worker.Pawn, "start", Location(worker.Location), new RouteTicks.Reachable(worker.ApproachTicks)));
            bindings[(id, worker.Pawn)] = new(id, worker.Pawn, [], PawnActivityKind.Work, worker.Target,
                worker.WorkGiver, worker.Job, 0, deadline, worker.Location)
                { SupportDemand = demand, Medicine = worker.Medicine?.Id ?? "", Recipe = surgery?.Recipe, Source = worker.Medicine?.Location };
        }
        foreach (var demand in read.Demands.Where(d => d.Kind is "care" or "surgery").OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            foreach (var service in read.Workers.Where(w => w.Demand == demand.Id && w.Job is "TendPatient" or "DoBill")
                .GroupBy(w => (Medicine: w.Medicine?.Id ?? "", Recipe: (w.Treatment as SupportTreatment.Surgery)?.Recipe.Bill ?? "")))
            {
                var care = "support/care/" + Key(demand.Id) + "/" + Key(service.Key.Medicine + service.Key.Recipe);
                var choice = "support/care/" + Key(demand.Id);
                if (!Space()) break;
                operations.Add(new(new(care, Location(demand.Location), Location(demand.Location), []), ServiceInputs(service.First()), []) { ChoiceGroup = choice });
                foreach (var worker in service) Worker(care, worker, demand.Id);
                roots.Add(new(care, []) { ActivityCompletions = [care], ExclusiveOutcome = choice });
            }
        }
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
                    options.Add(new(id, row.Pawn, Known(row.DeliveryTicks), Known(0d)));
                    routes.Add(new(row.Pawn, "start", Location(row.Source), new RouteTicks.Reachable(row.ApproachTicks)));
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
            if (worker.Medicine is not { } medicine) return [];
            var source = "support/observed/" + medicine.Id;
            if (observed.All(s => s.Id != source)) observed.Add(new(source, new(medicine.Resource), Known(medicine.Available), Location(medicine.Location), Known(0d), Known(horizon))
            { Lot = medicine.Id, Provenance = "native-selected-medicine" });
            return [new("medicine", new(medicine.Resource), Known(medicine.Count), Location(medicine.Location), ForecastRequirementKind.Consumed,
                Known<EquatableArray<string>>([source]), Known(0d), Known(horizon))];
        }
    }
}
