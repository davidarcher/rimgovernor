namespace RimGovernor.Brain.Policy;

// Binding the chosen transition (task 8.2 of redefine-habitat-requirements, Decision 4). The choice the transition comparator
// (ServicingChoiceRules.Transition) makes to take a standing building off its cell enters the LayoutPlan as one PlannedTransition:
// intent only (target, definition, cause, kind, the Relocate slot, the replacement it depends on). A proposing path reads it back
// (ServicingChoiceRules.Choose) instead of comparing again while the transition's own inputs hold, and compares afresh when they
// do not. Attempts and claims stay in the session journal; whether it is under way, refused or held is derived each review. It
// leaves the plan only when fresh facts show it done or moot (Settled), never on a receipt. No restoration ledger: nothing records
// what was removed, what is owed back or when a replacement served.

/// <summary>The replacement whose observed service a planned removal depends on: <see cref="Recipe"/> served at the plan's <see cref="Host"/> definition.</summary>
public sealed record ReplacementDependency(string Recipe, string Host);

/// <summary>The planned slot a Relocate moves its building to, named by the slot's minimum cell.</summary>
public sealed record RelocationSlot(Cell Minimum);

/// <summary>
/// A transition the plan committed to: the standing building <see cref="Target"/> of <see cref="Definition"/>, proposed for
/// <see cref="Cause"/>, taken off its cell by <see cref="Kind"/> (Relocate, Store or Remove). <see cref="Slot"/> is the planned
/// slot's minimum a Relocate moves it to; <see cref="Dependency"/> the replacement whose service a host spare's removal waits on.
/// </summary>
public sealed record PlannedTransition(string Target, string Definition, TransitionCause Cause, ServicingChoiceKind Kind)
{
    public RelocationSlot? Slot { get; init; }

    public ReplacementDependency? Dependency { get; init; }
}

/// <summary>A standing building as the settle rule reads it: its definition and the cells it occupies.</summary>
public sealed record StandingBuilding(string Definition, IReadOnlyCollection<Cell> Cells);

public static class TransitionBindingRules
{
    /// <summary>Whether <paramref name="transitions"/> are a plan's: one per target, named, a kind that takes the building off its cell, a slot exactly for a Relocate and a dependency only for a host spare.</summary>
    public static bool ValidTransitions(IReadOnlyList<PlannedTransition> transitions)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in transitions)
        {
            if (t.Target.Length == 0 || t.Definition.Length == 0 || !targets.Add(t.Target)
                || t.Kind is not (ServicingChoiceKind.Relocate or ServicingChoiceKind.Store or ServicingChoiceKind.Remove)
                || (t.Kind == ServicingChoiceKind.Relocate) != (t.Slot is not null)
                || (t.Dependency is not null && t.Cause != TransitionCause.HostRetirement))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The transition a decision binds: null when it takes nothing off its cell (Retain, a servicing choice, a hold, or a removal gated on its replacement).</summary>
    public static PlannedTransition? PlannedOf(TransitionCandidate c, TransitionDecision d) =>
        !d.Admitted ? null : new PlannedTransition(c.Target, c.Definition, c.Cause, d.Chosen!.Kind)
        {
            Slot = d.Chosen.Kind == ServicingChoiceKind.Relocate && c.Destination is { } destination ? new RelocationSlot(destination.Slot) : null,
            Dependency = c.Dependency,
        };

    /// <summary>
    /// Whether <paramref name="bound"/> still answers the proposal <paramref name="c"/>: the same definition proposed for the same
    /// cause, the same replacement still serving (the path names the dependency only while it serves), and for a Relocate the
    /// slot proposed for it is still its slot (the proposer offers only open slots, so a slot something else filled is not).
    /// </summary>
    public static bool Holds(PlannedTransition bound, TransitionCandidate c) =>
        bound.Target == c.Target && bound.Definition == c.Definition && bound.Cause == c.Cause && Equals(bound.Dependency, c.Dependency)
        && (bound.Kind != ServicingChoiceKind.Relocate || (c.Destination is { } destination && bound.Slot is { } slot && destination.Slot == slot.Minimum));

    /// <summary>
    /// The plan with each decision applied, later decisions for the same target winning: a decision that takes its building off
    /// its cell binds its transition in place of any earlier one; any other drops the earlier one (re-selected to stay). The same
    /// plan when nothing changes.
    /// </summary>
    public static LayoutPlan WithTransitions(this LayoutPlan plan, IEnumerable<TransitionDecision> decisions)
    {
        var next = plan.Transitions.ToList();
        foreach (var d in decisions)
        {
            next.RemoveAll(t => t.Target == d.Target);
            if (d.Planned is { } planned)
            {
                next.Add(planned);
            }
        }
        next.Sort((a, b) => string.CompareOrdinal(a.Target, b.Target));
        return next.SequenceEqual(plan.Transitions) ? plan : plan with { Transitions = next };
    }

    /// <summary>The replanned plan <paramref name="next"/> with <paramref name="prev"/>'s transitions: a replan never re-selects one; Settled drops those it made moot.</summary>
    public static LayoutPlan WithTransitionsOf(this LayoutPlan next, LayoutPlan prev) => next with { Transitions = prev.Transitions };

    /// <summary>The plan less the transitions of <paramref name="targets"/>.</summary>
    public static LayoutPlan WithoutTransitions(this LayoutPlan plan, IReadOnlyCollection<string> targets) =>
        plan with { Transitions = [.. plan.Transitions.Where(t => !targets.Contains(t.Target))] };

    /// <summary>
    /// The targets whose transition fresh facts show done or moot: the target no longer stands (stored, removed or minified on its
    /// way), it stands as another definition, a Relocate stands on its slot, or its cause is gone: retired ground the plan no
    /// longer retires, a planned room's ground the plan no longer plans, a host recipe the plan no longer hosts at the dependency's
    /// host, a spare bed a pawn now owns. <paramref name="standing"/> is the construction census by id; an accepted receipt never
    /// settles one.
    /// </summary>
    public static IReadOnlyList<string> Settled(LayoutPlan plan, IReadOnlyDictionary<string, StandingBuilding> standing, Func<string, bool> bedOwned)
    {
        var output = new List<string>();
        foreach (var t in plan.Transitions)
        {
            if (!standing.TryGetValue(t.Target, out var b) || b.Definition != t.Definition
                || (t.Kind == ServicingChoiceKind.Relocate && t.Slot is { } slot && b.Cells.Contains(slot.Minimum))
                || !CauseStands(plan, t, b, bedOwned))
            {
                output.Add(t.Target);
            }
        }
        return output;
    }

    /// <summary>
    /// Whether an open removal of <paramref name="target"/> a path proposed for one of <paramref name="causes"/> is settled on fresh
    /// facts alone, its binding gone (dropped in an earlier load, task 8.3): the building still stands and none of the causes stands
    /// for it (<see cref="Settled"/>'s cause rule, read as the transition its removal executes). A building no longer standing is
    /// left to its path: nothing proposes it again. A host spare's dependency is not recoverable from the removal, so its path
    /// governs every open retirement instead.
    /// </summary>
    public static bool SettledOpen(LayoutPlan plan, string target, IReadOnlyCollection<TransitionCause> causes, IReadOnlyDictionary<string, StandingBuilding> standing, Func<string, bool> bedOwned) =>
        causes.Count > 0 && standing.TryGetValue(target, out var b)
        && causes.All(cause => cause != TransitionCause.HostRetirement && !CauseStands(plan, new PlannedTransition(target, b.Definition, cause, ServicingChoiceKind.Remove), b, bedOwned));

    private static bool CauseStands(LayoutPlan plan, PlannedTransition t, StandingBuilding b, Func<string, bool> bedOwned)
    {
        if (t.Cause == TransitionCause.RetiredGround)
        {
            return plan.RetiredGround.Any(g => b.Cells.Any(g.Contains));
        }
        if (t.Cause is TransitionCause.PlannedGround or TransitionCause.StaleInterior)
        {
            return plan.Rooms.Any(r => b.Cells.Any(r.GroundRegion().Contains));
        }
        if (t.Cause == TransitionCause.HostRetirement)
        {
            return t.Dependency is { } d
                && plan.Rooms.SelectMany(r => r.Production).Any(p => p.Recipe == d.Recipe && p.Bench == d.Host && p.Step == HostStep.Host);
        }
        if (t.Cause == TransitionCause.SpareBed)
        {
            return !bedOwned(t.Target);
        }
        throw new InvalidOperationException("transition cause: " + t.Cause);
    }

    /// <summary>
    /// The stale removal evidence among <paramref name="open"/> (each an open removal Method and the building its action takes
    /// down): those whose target the plan carries no transition for, a choice re-selected, settled or never planned in this
    /// timeline (a rewind to before it was planned). Their unissued actions are cancelled; nothing they report closes a transition.
    /// </summary>
    public static IReadOnlyList<T> Stale<T>(LayoutPlan plan, IEnumerable<T> open, Func<T, string> target)
    {
        var bound = plan.Transitions.Select(t => t.Target).ToHashSet(StringComparer.Ordinal);
        return [.. open.Where(o => !bound.Contains(target(o)))];
    }
}
