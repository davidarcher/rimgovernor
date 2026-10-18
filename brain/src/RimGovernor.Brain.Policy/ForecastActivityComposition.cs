namespace RimGovernor.Brain.Policy;

public enum ForecastRequirementKind { Consumed, Reusable, Exclusive }

/// <summary>Native providers certify allowed physical sources, including access and material legality. Units are discrete items.</summary>
public sealed record ForecastCompositionRequirement(string Id, Resource Resource, Fact<int> Units, string Location,
    ForecastRequirementKind Kind, Fact<EquatableArray<string>> Sources, Fact<double> EarliestUse, Fact<double> LatestUse);

/// <summary>A physical supply at one location. Movement must be a supplied operation consuming its earlier location.</summary>
public sealed record ForecastCompositionSupply(string Id, Resource Resource, Fact<int> Units, string Location,
    Fact<double> AvailableAt, Fact<double> ExpiresAt)
{
    public string Producer { get; init; } = "";
    public string Lot { get; init; } = "";
    public string Provenance { get; init; } = "";
    public ForecastRequirementKind Kind { get; init; }
    public string Capacity { get; init; } = "";
}

public sealed record ForecastCompositionOperation(ForecastActivity Activity,
    EquatableArray<ForecastCompositionRequirement> Requirements, EquatableArray<ForecastCompositionSupply> Supplies)
{
    public string ChoiceGroup { get; init; } = "";
}

public sealed record ForecastCompositionAllocation(string Operation, string Requirement, string Source, int Units,
    string Producer, string Provenance, ForecastRequirementKind Kind, string Capacity);
public sealed record ForecastCompositionGraph(EquatableArray<ForecastActivity> Activities,
    EquatableArray<ForecastCompositionAllocation> Allocations);
public sealed record ForecastCompositionRequest
{
    public EquatableArray<ForecastCompositionOperation> Operations { get; init; }
    public EquatableArray<ForecastCompositionSupply> Observed { get; init; }
    public EquatableArray<string> Roots { get; init; }
    /// <summary>Demand requirements have no actor or physical activity; their allocation Operation is empty.</summary>
    public EquatableArray<ForecastCompositionRequirement> RootRequirements { get; init; }
    public double Horizon { get; init; }
    public int ExpansionBudget { get; init; } = 4096;
    public int DepthLimit { get; init; } = 16;
    public int ResultLimit { get; init; } = 128;
    public ForecastCompositionGraph? Incumbent { get; init; }
}
public sealed record ForecastCompositionResult(EquatableArray<ForecastCompositionGraph> Graphs,
    EquatableArray<string> Findings, int Expansions, bool Truncated);

public static class ForecastActivityComposition
{
    /// <summary>Bounded backward requirement closure. No scheduling, valuation, resource-name inference or mutable inventory owner.</summary>
    public static ForecastCompositionResult ComposeActivityDependencies(ForecastCompositionRequest request)
    {
        if (!double.IsFinite(request.Horizon) || request.Horizon < 0 || request.ExpansionBudget is < 0 or > 65536
            || request.DepthLimit is < 1 or > 64 || request.ResultLimit is < 1 or > 512
            || request.Operations.Count > 512 || request.Observed.Count > 2048
            || request.RootRequirements.Count + request.Operations.Sum(o => o.Requirements.Count) > 4096 || request.Operations.Sum(o => o.Supplies.Count) > 4096
            || request.Operations.SelectMany(o => o.Requirements).Concat(request.RootRequirements).Any(r => r.Sources.IsKnown && r.Sources.Value.Count > 2048))
            throw new DomainException("invalid activity composition bounds");
        var operations = request.Operations.ToDictionary(o => o.Activity.Id, StringComparer.Ordinal);
        if (operations.ContainsKey("")) throw new DomainException("empty physical operation identity");
        if (request.RootRequirements.Count != 0)
            operations.Add("", new(new("", "", "", []), request.RootRequirements, []));
        var supplies = request.Observed.Concat(request.Operations.SelectMany(o => o.Supplies)).ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (request.Observed.Any(s => s.Producer.Length != 0)
            || operations.Values.Any(o => o.Supplies.Any(s => s.Producer != o.Activity.Id)
                || o.Requirements.Select(r => r.Id).Distinct().Count() != o.Requirements.Count)
            || supplies.Values.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Units.IsKnown && s.Units.Value < 0)
            || operations.Values.Any(o => o.Requirements.Any(r => r.Units.IsKnown && r.Units.Value <= 0)))
            throw new DomainException("invalid activity composition identities or units");
        if (supplies.Values.GroupBy(s => (s.Producer, s.Lot.Length == 0 ? s.Id : s.Lot, s.Location, s.Resource)).Any(g => g.Count() > 1))
            throw new DomainException("duplicate physical composition supply");
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var graphs = new Dictionary<string, ForecastCompositionGraph>(StringComparer.Ordinal);
        var expansions = 0; var truncated = false;
        bool Spend()
        {
            if (expansions >= request.ExpansionBudget) { truncated = true; return false; }
            expansions++; return true;
        }
        bool Add(HashSet<string> selected, string id, HashSet<string> path)
        {
            if (!operations.TryGetValue(id, out var operation)) { findings.Add("unsupported_operation:" + id); return false; }
            if (path.Contains(id)) { findings.Add("cyclic_composition:" + id); return false; }
            if (selected.Contains(id)) return true;
            if (path.Count >= request.DepthLimit || selected.Count >= 64) { truncated = true; findings.Add("composition_depth_or_size_bound"); return false; }
            var choice = operation.ChoiceGroup.Length != 0 ? operation.ChoiceGroup : operation.Activity.ChoiceIdentity;
            if (choice.Length != 0 && selected.Any(s => (operations[s].ChoiceGroup.Length != 0 ? operations[s].ChoiceGroup : operations[s].Activity.ChoiceIdentity) == choice))
            { findings.Add("conflicting_composition_choice:" + choice); return false; }
            selected.Add(id); path.Add(id);
            foreach (var prerequisite in operation.Activity.Prerequisites.Order(StringComparer.Ordinal))
                if (!Add(selected, prerequisite, path)) return false;
            path.Remove(id); return true;
        }
        bool Link(ForecastCompositionRequirement requirement, ForecastCompositionSupply source)
        {
            if (!requirement.Units.IsKnown || !requirement.Sources.IsKnown || !requirement.EarliestUse.TryGetValue(out var earliest)
                || !requirement.LatestUse.TryGetValue(out var latest) || !source.Units.IsKnown
                || !source.AvailableAt.TryGetValue(out var available) || !source.ExpiresAt.TryGetValue(out var expiry)
                || source.Provenance.Length == 0)
            { findings.Add("unknown_composition_link:" + requirement.Id + ":" + source.Id); return false; }
            if (!double.IsFinite(earliest) || !double.IsFinite(latest) || !double.IsFinite(available) || !double.IsFinite(expiry)
                || earliest < 0 || available < 0 || latest < earliest || expiry < available)
                throw new DomainException("invalid composition time bounds");
            if (source.Kind == ForecastRequirementKind.Consumed && source.Producer.Length != 0)
            {
                var output = operations[source.Producer].Activity.Outputs.SingleOrDefault(o => o.Resource == source.Resource
                    && (o.Lot.Length == 0 ? source.Producer : o.Lot) == (source.Lot.Length == 0 ? source.Id : source.Lot)
                    && (o.LotLocation.Length == 0 ? operations[source.Producer].Activity.EndLocation : o.LotLocation) == source.Location);
                if (output is null || !output.Units.TryGetValue(out var yield) || yield < source.Units.Value)
                { findings.Add("unsupported_composition_yield:" + source.Id); return false; }
            }
            return source.Kind == requirement.Kind && source.Resource == requirement.Resource && source.Location == requirement.Location
                && requirement.Sources.Value.Contains(source.Id) && source.Units.Value > 0
                && Math.Max(earliest, available) <= Math.Min(request.Horizon, Math.Min(latest, expiry))
                && (requirement.Kind != ForecastRequirementKind.Exclusive || source.Capacity.Length != 0 && requirement.Units.Value == 1 && source.Units.Value == 1);
        }
        ForecastCompositionGraph? Complete(HashSet<string> selected, List<ForecastCompositionAllocation> allocated)
        {
            if (!request.Roots.All(selected.Contains) || request.RootRequirements.Count != 0 && !selected.Contains("")) return null;
            foreach (var id in selected)
            {
                var op = operations[id];
                if (!op.Activity.Prerequisites.All(selected.Contains)) return null;
                foreach (var req in op.Requirements)
                {
                    var links = allocated.Where(a => a.Operation == id && a.Requirement == req.Id).ToArray();
                    if (!req.Units.IsKnown || links.Sum(a => (long)a.Units) != req.Units.Value) return null;
                    if (links.Select(a => a.Source).Distinct().Count() != links.Length) return null;
                    foreach (var link in links)
                        if (!supplies.TryGetValue(link.Source, out var source) || !Link(req, source) || link.Units <= 0
                            || link.Units > source.Units.Value || link.Producer != source.Producer || link.Provenance != source.Provenance
                            || link.Kind != source.Kind || link.Capacity != source.Capacity
                            || source.Producer.Length != 0 && !selected.Contains(source.Producer)) return null;
                }
            }
            if (allocated.Any(a => !selected.Contains(a.Operation) || !operations[a.Operation].Requirements.Any(r => r.Id == a.Requirement))) return null;
            if (allocated.Where(a => a.Kind == ForecastRequirementKind.Consumed).GroupBy(a => a.Source)
                .Any(g => g.Sum(a => (long)a.Units) > supplies[g.Key].Units.Value)) return null;
            var dependencies = selected.ToDictionary(id => id, id => operations[id].Activity.Prerequisites
                .Concat(allocated.Where(a => a.Operation == id && a.Producer.Length != 0).Select(a => a.Producer)).Distinct().Order(StringComparer.Ordinal).ToArray());
            var depths = new Dictionary<string, int>(StringComparer.Ordinal);
            int Depth(string id, HashSet<string> path)
            {
                if (depths.TryGetValue(id, out var cached)) return cached;
                if (!path.Add(id)) { findings.Add("cyclic_composition:" + id); return -1; }
                var depth = id.Length == 0 ? 0 : 1;
                foreach (var parent in dependencies[id])
                {
                    var previous = Depth(parent, path);
                    if (previous < 0) return -1;
                    depth = Math.Max(depth, previous + (id.Length == 0 ? 0 : 1));
                }
                path.Remove(id);
                if (depth > request.DepthLimit) { findings.Add("composition_depth_or_size_bound"); truncated = true; return -1; }
                depths[id] = depth; return depth;
            }
            if (selected.Any(id => Depth(id, []) < 0)) return null;
            var activities = selected.Where(id => id.Length != 0).Order(StringComparer.Ordinal).Select(id =>
            {
                var op = operations[id]; var links = allocated.Where(a => a.Operation == id).ToArray();
                var earliest = op.Requirements.Select(r => r.EarliestUse.Value).Concat(links.Select(a => supplies[a.Source].AvailableAt.Value))
                    .Append(op.Activity.NotBefore).Max();
                var latest = op.Requirements.Select(r => r.LatestUse.Value).Concat(links.Select(a => supplies[a.Source].ExpiresAt.Value))
                    .Append(op.Activity.LatestStart ?? request.Horizon).Min();
                var finish = op.Requirements.Where(r => r.Kind != ForecastRequirementKind.Consumed).Select(r => r.LatestUse.Value)
                    .Concat(links.Where(a => a.Kind != ForecastRequirementKind.Consumed).Select(a => supplies[a.Source].ExpiresAt.Value))
                    .Append(op.Activity.LatestFinish ?? request.Horizon).Min();
                var inputs = links.Where(a => a.Kind == ForecastRequirementKind.Consumed).Select(a =>
                {
                    var source = supplies[a.Source];
                    return new ForecastActivityYield(source.Resource, Known((double)a.Units))
                    { Lot = source.Lot.Length == 0 ? source.Id : source.Lot, LotLocation = source.Location };
                });
                return op.Activity with { ChoiceIdentity = op.ChoiceGroup.Length == 0 ? op.Activity.ChoiceIdentity : op.ChoiceGroup, Prerequisites = dependencies[id], NotBefore = earliest, LatestStart = latest, LatestFinish = finish,
                    Inputs = op.Requirements.Any(r => r.Kind == ForecastRequirementKind.Consumed) ? EquatableArray.From(inputs.GroupBy(i => (i.Resource, i.Lot, i.LotLocation)).Select(g => g.First() with { Units = Known(g.Sum(i => i.Units.Value)) })) : op.Activity.Inputs };
            }).ToArray();
            if (activities.Any(a => a.NotBefore > a.LatestStart)) return null;
            return new(EquatableArray.From(activities), EquatableArray.From(allocated.OrderBy(a => a.Operation, StringComparer.Ordinal)
                .ThenBy(a => a.Requirement, StringComparer.Ordinal).ThenBy(a => a.Source, StringComparer.Ordinal)));
        }
        string Key(ForecastCompositionGraph graph) => string.Join(";", graph.Activities.Select(a => a.Id)) + "|" +
            string.Join(";", graph.Allocations.Select(a => a.Operation + "/" + a.Requirement + "/" + a.Source + "=" + a.Units));
        if (request.Incumbent is { } incumbent)
        {
            var chosen = new HashSet<string>(StringComparer.Ordinal);
            if ((request.RootRequirements.Count == 0 || Add(chosen, "", [])) && incumbent.Activities.All(a => Add(chosen, a.Id, [])) && Complete(chosen, incumbent.Allocations.ToList()) is { } valid)
            graphs.Add(Key(valid), valid);
            else findings.Add("invalid_composition_incumbent");
        }
        void Search(HashSet<string> selected, List<ForecastCompositionAllocation> allocated, HashSet<(string, string)> resolved)
        {
            if (!Spend()) return;
            var pending = selected.Order(StringComparer.Ordinal).SelectMany(id => operations[id].Requirements.OrderBy(r => r.Id, StringComparer.Ordinal)
                .Where(r => !resolved.Contains((id, r.Id))).Select(r => (Id: id, Requirement: r))).FirstOrDefault();
            if (pending.Requirement is null)
            {
                if (Complete(selected, allocated) is not { } graph) return;
                if (graphs.ContainsKey(Key(graph))) return;
                if (graphs.Count == request.ResultLimit) { truncated = true; return; }
                graphs.Add(Key(graph), graph); return;
            }
            var requirement = pending.Requirement;
            if (!requirement.Sources.IsKnown || !requirement.Units.IsKnown || !requirement.EarliestUse.IsKnown || !requirement.LatestUse.IsKnown)
            { findings.Add("unknown_composition_requirement:" + pending.Id + ":" + requirement.Id); return; }
            var availableSources = new List<ForecastCompositionSupply>();
            foreach (var id in requirement.Sources.Value.Distinct().Order(StringComparer.Ordinal))
            {
                if (!Spend()) return;
                if (!supplies.TryGetValue(id, out var supply)) findings.Add("unsupported_composition_source:" + id);
                else if (Link(requirement, supply)) availableSources.Add(supply);
            }
            var candidates = availableSources.ToArray();
            var fulfilled = false;
            void Allocate(int index, int remaining, List<ForecastCompositionAllocation> links)
            {
                if (!Spend()) return;
                if (remaining == 0)
                {
                    var next = new HashSet<string>(selected, StringComparer.Ordinal);
                    foreach (var link in links.Where(a => a.Producer.Length != 0)) if (!Add(next, link.Producer, [])) return;
                    fulfilled = true;
                    Search(next, [.. allocated, .. links], [.. resolved, (pending.Id, requirement.Id)]); return;
                }
                if (index == candidates.Length) return;
                var source = candidates[index];
                var spent = requirement.Kind == ForecastRequirementKind.Consumed
                    ? allocated.Where(a => a.Source == source.Id).Sum(a => (long)a.Units) : 0;
                var maximum = (int)Math.Min(remaining, source.Units.Value - spent);
                for (var count = maximum; count >= 0; count--)
                {
                    if (truncated && expansions >= request.ExpansionBudget) return;
                    if (requirement.Kind != ForecastRequirementKind.Consumed && count != 0 && count != remaining) continue;
                    // A producer that cannot join the selection (same physical choice, cycle, bound) kills every partition using it;
                    // skip it here, not after the partitions are enumerated, or its siblings exhaust the budget first.
                    if (count > 0 && source.Producer.Length != 0)
                    {
                        var probe = new HashSet<string>(selected, StringComparer.Ordinal);
                        if (!links.Where(a => a.Producer.Length != 0).Select(a => a.Producer).Append(source.Producer).All(producer => Add(probe, producer, []))) continue;
                    }
                    if (count > 0) links.Add(new(pending.Id, requirement.Id, source.Id, count, source.Producer, source.Provenance, source.Kind, source.Capacity));
                    Allocate(index + 1, remaining - count, links);
                    if (count > 0) links.RemoveAt(links.Count - 1);
                }
            }
            Allocate(0, requirement.Units.Value, []);
            if (!fulfilled) findings.Add("unsatisfied_composition_requirement:" + pending.Id + ":" + requirement.Id);
        }
        var initial = new HashSet<string>(StringComparer.Ordinal);
        if ((request.RootRequirements.Count == 0 || Add(initial, "", [])) && request.Roots.Order(StringComparer.Ordinal).All(root => Add(initial, root, []))) Search(initial, [], []);
        if (truncated) findings.Add("composition_truncated");
        return new(EquatableArray.From(graphs.Values), EquatableArray.From(findings), expansions, truncated);
    }
}
