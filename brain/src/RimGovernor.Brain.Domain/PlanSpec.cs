namespace RimGovernor.Brain.Domain;

/// <summary>
/// Requires observed completion of another action in this plan. Dispatch still checks native legality and
/// colony invariants; prior completion does not prove the resulting object still exists.
/// Coupled means the dependent order uses a result of its prerequisite, such as a created identity or reached
/// position. It is planned at the first step after that outcome, ahead of the planner cadence, and admitted
/// against native CAS evidence. Ordering alone is not coupling.
/// </summary>
public readonly record struct ActionDependency(ActionId Action, ActionId Requires, bool Coupled = false);

/// <summary>
/// An immutable, validated set of actions with their dependencies (Go <c>PlanSpec</c>). The constructor is
/// Go's <c>NewPlan</c>. Actions can only exist in canonical form, so unlike Go there is no per-kind
/// re-canonicalisation here; the plan checks identity, duplicates, family prerequisites and dependencies.
/// </summary>
public sealed record PlanSpec
{
    public PlanSpec(PlanId id, PlanRevision revision, IEnumerable<DomainAction> actions, IEnumerable<ActionDependency>? dependencies = null)
    {
        DomainException.Require(id.IsValid, "invalid plan identity");
        var list = actions.ToList();
        var seen = new Dictionary<ActionId, DomainAction>();
        foreach (var a in list)
        {
            DomainException.Require(!seen.ContainsKey(a.Id), $"duplicate action identity \"{a.Id}\"");
            if (a.Subdue is { } subdue)
            {
                var ok = seen.TryGetValue(subdue.DraftAction, out var prerequisite) && prerequisite.OwnedDraft is { } draft && draft.Pawn == subdue.Pawn;
                DomainException.Require(ok, "subdue requires its preceding owned draft for the same pawn");
            }
            if (a.Movement is { } movement)
            {
                var ok = seen.TryGetValue(movement.DraftAction, out var prerequisite) && prerequisite.OwnedDraft is { } draft && draft.Pawn == movement.Pawn;
                DomainException.Require(ok, "movement requires its preceding owned draft for the same pawn");
            }
            if (a.WallRemoval is { BackupOf.IsEmpty: false } removal)
            {
                var ok = seen.TryGetValue(removal.BackupOf, out var prerequisite) && prerequisite.Building is { } building && building.Definition == "Wall" && building.Cell == removal.Cell;
                DomainException.Require(ok, "backup wall removal requires its preceding backup wall at the same cell in the same bundle");
            }
            seen[a.Id] = a;
        }
        var deps = ValidateDependencies(seen, dependencies ?? []);
        foreach (var a in list)
        {
            if (a.WallRemoval is { BackupOf.IsEmpty: false } removal)
            {
                // Go compares the whole struct, so only an uncoupled dependency satisfies this.
                DomainException.Require(deps.Contains(new ActionDependency(a.Id, removal.BackupOf)), "backup wall removal must declare its backup wall as a dependency");
            }
            if (a.Capture is { Arrest: true } capture)
            {
                var owned = false;
                foreach (var dep in deps)
                {
                    owned = owned || (dep.Action == a.Id && dep.Coupled && seen[dep.Requires].OwnedDraft is { } draft && draft.Pawn == capture.Capturer);
                }
                DomainException.Require(owned, "arrest requires a coupled owned draft for its capturer");
            }
        }
        Id = id;
        Revision = revision;
        Actions = EquatableArray.From(list);
        Dependencies = EquatableArray.From(deps);
    }

    public PlanId Id { get; }

    public PlanRevision Revision { get; }

    public EquatableArray<DomainAction> Actions { get; }

    /// <summary>Dependencies in canonical (action, requires) order.</summary>
    public EquatableArray<ActionDependency> Dependencies { get; }

    /// <summary>The plan action with the id (Go <c>PlanSpec.Action</c>); null when absent.</summary>
    public DomainAction? Action(ActionId id)
    {
        foreach (var a in Actions)
        {
            if (a.Id == id)
            {
                return a;
            }
        }
        return null;
    }

    public void Validate() => _ = new PlanSpec(Id, Revision, Actions, Dependencies);

    private static List<ActionDependency> ValidateDependencies(Dictionary<ActionId, DomainAction> actions, IEnumerable<ActionDependency> dependencies)
    {
        var input = dependencies.ToList();
        DomainException.Require(input.Count <= 4096, "too many action dependencies");
        var deps = input.OrderBy(d => d.Action).ThenBy(d => d.Requires).ToList();
        var graph = new Dictionary<ActionId, List<ActionId>>();
        for (var i = 0; i < deps.Count; i++)
        {
            var d = deps[i];
            var invalid =
                !actions.ContainsKey(d.Action)
                || !actions.ContainsKey(d.Requires)
                || d.Action == d.Requires
                || (i > 0 && d.Action == deps[i - 1].Action && d.Requires == deps[i - 1].Requires);
            DomainException.Require(!invalid, "invalid or duplicate dependency");
            if (!graph.TryGetValue(d.Action, out var required))
            {
                graph[d.Action] = required = [];
            }
            required.Add(d.Requires);
        }
        // Subdue and movement already have a mandatory draft prerequisite. Include it in cycle detection.
        foreach (var (id, a) in actions)
        {
            if (a.Subdue is { } subdue)
            {
                AddEdge(graph, id, subdue.DraftAction);
            }
            if (a.Movement is { } movement)
            {
                AddEdge(graph, id, movement.DraftAction);
            }
        }
        var visiting = new HashSet<ActionId>();
        var done = new HashSet<ActionId>();
        bool Visit(ActionId id)
        {
            if (visiting.Contains(id))
            {
                return false;
            }
            if (done.Contains(id))
            {
                return true;
            }
            visiting.Add(id);
            if (graph.TryGetValue(id, out var required))
            {
                foreach (var next in required)
                {
                    if (!Visit(next))
                    {
                        return false;
                    }
                }
            }
            visiting.Remove(id);
            done.Add(id);
            return true;
        }
        foreach (var id in graph.Keys.ToList())
        {
            DomainException.Require(Visit(id), "cyclic action dependency");
        }
        return deps;
    }

    private static void AddEdge(Dictionary<ActionId, List<ActionId>> graph, ActionId from, ActionId to)
    {
        if (!graph.TryGetValue(from, out var required))
        {
            graph[from] = required = [];
        }
        required.Add(to);
    }

    /// <summary>
    /// Names the actions of this plan's coupled dependencies that are ready to dispatch: every prerequisite has
    /// completed in the current world and the action itself has not been dispatched. They are the orders a
    /// running clock window plans live for at once; a plan without coupled dependencies never has any.
    /// </summary>
    public IReadOnlyList<ActionId> CoupledPending(IReadOnlyList<Progress> progress, GenerationSnapshot current, Tick tick)
    {
        var ready = new List<ActionId>();
        var seen = new HashSet<ActionId>();
        foreach (var d in Dependencies)
        {
            if (!d.Coupled || !seen.Add(d.Action))
            {
                continue;
            }
            foreach (var v in progress)
            {
                var s = v.View;
                if (s.Action != d.Action || s.Unresolved || (s.Stage != Stage.Pending && s.Stage != Stage.Prepared))
                {
                    continue;
                }
                if (DependenciesMet(d.Action, progress, current, tick))
                {
                    ready.Add(d.Action);
                }
            }
        }
        return ready;
    }

    /// <summary>Throws <see cref="DependencyException"/> unless every prerequisite of <paramref name="action"/> has completed in <paramref name="current"/>.</summary>
    public void CheckDependencies(ActionId action, IReadOnlyList<Progress> progress, GenerationSnapshot current, Tick tick)
    {
        if (!DependenciesMet(action, progress, current, tick))
        {
            throw new DependencyException();
        }
    }

    /// <summary>The boolean form of <see cref="CheckDependencies"/>.</summary>
    public bool DependenciesMet(ActionId action, IReadOnlyList<Progress> progress, GenerationSnapshot current, Tick tick)
    {
        if (!current.IsValid || current.Plan != Id || current.Revision != Revision || tick.Value < 0)
        {
            return false;
        }
        if (Action(action) is null)
        {
            return false;
        }
        var byId = new Dictionary<ActionId, Progress>();
        foreach (var v in progress)
        {
            if (!byId.TryAdd(v.View.Action, v))
            {
                return false;
            }
        }
        foreach (var d in Dependencies)
        {
            if (d.Action != action)
            {
                continue;
            }
            if (!byId.TryGetValue(d.Requires, out var v))
            {
                return false;
            }
            var s = v.View;
            if (
                s.Plan != Id
                || s.Revision != Revision
                || s.Stage != Stage.Completed
                || s.Unresolved
                || !s.Effect.TryGetValue(out var effect)
                || effect != Effect.Completed
                || !s.Snapshot.SameWorld(current)
                || s.Tick > tick
            )
            {
                return false;
            }
        }
        return true;
    }
}
