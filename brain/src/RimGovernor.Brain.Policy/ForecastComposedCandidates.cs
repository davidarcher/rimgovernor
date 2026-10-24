using RimGovernor.Brain.Policy.Methods;

namespace RimGovernor.Brain.Policy;

internal sealed partial record ForecastComposedCandidates(ForecastActivity[] Tasks, ForecastActivityOption[] Options,
    ForecastNeedTask[] Needs, ForecastCommitment[] Alternatives,
    IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings,
    EquatableArray<string> Findings, bool Truncated)
{
    public IReadOnlyDictionary<string, string[]> Realizations { get; init; } = new Dictionary<string, string[]>();
    public int Expansions { get; init; }
}

internal sealed partial record ForecastComposedCandidates
{
    /// <summary>Both production families use the same dependency closure and realization interning.
    /// Family adapters supply native eligibility and action templates; they do not select sources.</summary>
    internal static ForecastComposedCandidates ComposeCoupledCandidates(
        IEnumerable<ForecastCompositionOperation> suppliedOperations, IEnumerable<ForecastCompositionSupply> suppliedStock,
        IEnumerable<ForecastCommitment> suppliedRoots, IEnumerable<ForecastActivityOption> suppliedOptions,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> suppliedBindings,
        IEnumerable<ForecastNeedTask> suppliedNeeds, double horizon,
        IReadOnlyDictionary<string, EquatableArray<ForecastCompositionRequirement>>? rootRequirements = null)
    {
        var sourceAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var stockRows = suppliedStock.GroupBy(s => (Lot: s.Lot.Length == 0 ? s.Id : s.Lot, s.Location, s.Resource)).Select(group =>
        {
            var rows = group.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
            var first = rows[0];
            foreach (var row in rows) sourceAliases[row.Id] = first.Id;
            return first with
            {
                // Reads that disagree on one lot's count offer none of it.
                Units = rows.Select(r => r.Units).Distinct().Count() == 1 ? first.Units : 0,
                AvailableAt = rows.Max(r => r.AvailableAt),
                ExpiresAt = rows.Min(r => r.ExpiresAt),
                Provenance = string.Join(";", rows.Select(r => r.Provenance).Distinct().Order(StringComparer.Ordinal))
            };
        }).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        ForecastCompositionRequirement Canonical(ForecastCompositionRequirement requirement) => requirement with
        {
            Sources = EquatableArray.From(requirement.Sources.Select(s => sourceAliases.GetValueOrDefault(s, s)).Distinct().Order(StringComparer.Ordinal))
        };
        var operationRows = suppliedOperations.Select(o => o with { Requirements = EquatableArray.From(o.Requirements.Select(Canonical)) })
            .OrderBy(o => o.Activity.Id, StringComparer.Ordinal).ToArray();
        var operations = operationRows.Take(512).ToArray();
        var stock = stockRows.Take(2048).ToArray();
        var supplies = stock.Concat(operations.SelectMany(o => o.Supplies)).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var templates = suppliedOptions.ToLookup(o => o.Activity);
        var needs = suppliedNeeds.ToLookup(n => n.Task);
        var tasks = new Dictionary<string, ForecastActivity>(StringComparer.Ordinal);
        var interned = new Dictionary<string, string>(StringComparer.Ordinal);
        var realizations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var options = new List<ForecastActivityOption>(); var effects = new List<ForecastNeedTask>();
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        var roots = new List<ForecastCommitment>(); var findings = new SortedSet<string>(StringComparer.Ordinal);
        var truncated = operationRows.Length > operations.Length || stockRows.Length > stock.Length; var remaining = 16384;
        var orderedRoots = suppliedRoots.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        for (var rootIndex = 0; rootIndex < orderedRoots.Length; rootIndex++)
        {
            if (remaining == 0)
            { truncated = true; findings.Add("unvisited_composition_roots:" + (orderedRoots.Length - rootIndex)); break; }
            var root = orderedRoots[rootIndex];
            // Reserve an opportunity for later independent roots. An expensive first
            // branch must not consume the entire census before any other root is tried.
            var allowance = Math.Min(2048, Math.Max(1, remaining / (orderedRoots.Length - rootIndex)));
            var composed = ForecastActivityComposition.ComposeActivityDependencies(new()
            {
                Operations = EquatableArray.From(operations), Observed = EquatableArray.From(stock),
                Roots = root.ActivityCompletions, Horizon = horizon,
                RootRequirements = EquatableArray.From((rootRequirements?.GetValueOrDefault(root.Id) ?? []).Select(Canonical)),
                ExpansionBudget = allowance, ResultLimit = 32
            });
            remaining -= composed.Expansions; truncated |= composed.Truncated; findings.UnionWith(composed.Findings);
            if (composed.Truncated) findings.Add("root_composition_truncated:" + root.Id);
            foreach (var graph in composed.Graphs)
            {
                // Reserve generation room for current needs; omitted realizations remain explicitly unassessed.
                if (tasks.Count + graph.Activities.Count > 192) { truncated = true; continue; }
                var original = graph.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal);
                var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
                string Intern(string id)
                {
                    if (mapped.TryGetValue(id, out var mappedId)) return mappedId;
                    var task = original[id];
                    var dependencies = task.Prerequisites.Select(Intern).Order(StringComparer.Ordinal).ToArray();
                    var allocations = graph.Allocations.Where(a => a.Operation == id).OrderBy(a => a.Requirement, StringComparer.Ordinal)
                        .ThenBy(a => a.Source, StringComparer.Ordinal).ToArray();
                    var key = id + "|" + string.Join(",", dependencies) + "|" + string.Join(";", allocations.Select(a =>
                        a.Requirement + ":" + a.Source + ":" + a.Units));
                    if (!interned.TryGetValue(key, out mappedId))
                    {
                        mappedId = id + "/composition/" + interned.Count;
                        interned.Add(key, mappedId);
                        if (!realizations.TryGetValue(id, out var variants)) realizations[id] = variants = [];
                        variants.Add(mappedId);
                        tasks.Add(mappedId, task with { Id = mappedId, Prerequisites = dependencies,
                            ChoiceIdentity = task.ChoiceIdentity.Length == 0 ? id : task.ChoiceIdentity });
                        foreach (var option in templates[id])
                        {
                            options.Add(option with { Activity = mappedId, Equipment = EquatableArray.From(option.Equipment.Concat(
                                allocations.Where(a => a.Kind == ForecastRequirementKind.Exclusive && a.Capacity.Length != 0)
                                    .Select(a => a.Capacity)).Distinct().Order(StringComparer.Ordinal)) });
                            if (suppliedBindings.TryGetValue((id, option.Pawn), out var binding))
                                bindings[(mappedId, option.Pawn)] = binding with { Id = mappedId, Prerequisites = EquatableArray.From(dependencies),
                                    SelectedInputs = EquatableArray.From(task.Inputs.Select(input =>
                                    {
                                        var links = allocations.Where(a => a.Kind == ForecastRequirementKind.Consumed && supplies[a.Source] is var source
                                            && source.Resource == input.Resource && (source.Lot.Length == 0 ? source.Id : source.Lot) == input.Lot).ToArray();
                                        return new SelectedActivityInput(input.Resource.Value, input.Lot, checked((int)input.Units),
                                            binding.Source ?? binding.Location, string.Join(";", links.Select(a => a.Provenance)))
                                        { Observed = links.Length > 0 && links.All(a => a.Producer.Length == 0),
                                            Producers = EquatableArray.From(links.Where(a => a.Producer.Length != 0)
                                                .Select(a => Intern(a.Producer)).Distinct().Order(StringComparer.Ordinal)) };
                                    })) };
                        }
                        mapped[id] = mappedId;
                        effects.AddRange(needs[id].Select(n => n with { Task = mappedId,
                            FoodReady = n.FoodReady.Length == 0 || !original.ContainsKey(n.FoodReady) ? "" : Intern(n.FoodReady),
                            FreshFrom = n.FreshFrom.Length == 0 || !original.ContainsKey(n.FreshFrom) ? "" : Intern(n.FreshFrom) }));
                    }
                    mapped[id] = mappedId;
                    return mappedId;
                }
                var completions = root.ActivityCompletions.Concat(graph.Allocations.Where(a => a.Operation.Length == 0
                    && a.Producer.Length != 0).Select(a => a.Producer)).Distinct().Select(Intern).ToArray();
                var variant = root.Id + "/composition/" + roots.Count;
                roots.Add(root with { Id = variant, ActivityCompletions = EquatableArray.From(completions),
                    Outcome = root.Outcome is { } outcome ? outcome with { Id = variant,
                        Completion = outcome.Completion.Length == 0 ? "" : mapped[outcome.Completion] } : null });
            }
        }
        if (truncated) findings.Add("shared_composition_truncated");
        return new(tasks.Values.ToArray(), options.ToArray(), effects.ToArray(), roots.ToArray(), bindings,
            EquatableArray.From(findings), truncated)
        { Realizations = realizations.ToDictionary(p => p.Key, p => p.Value.ToArray()), Expansions = 16384 - remaining };
    }
}
