using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>
    /// Whether an open removal's target is settled for a path proposing it for <paramref name="causes"/>, derived from this review's
    /// census and plan alone (TransitionBindingRules.SettledOpen), so a removal whose transition an earlier load dropped is still
    /// recognised stale after a reload: nothing settled is remembered. Nothing is settled while the census is unread.
    /// </summary>
    internal static Func<string, bool> SettledOf(ColonyReading reading, params TransitionCause[] causes)
    {
        if (reading.Facts.LayoutPlan is not { } plan)
        {
            return _ => false;
        }
        IReadOnlyDictionary<string, StandingBuilding> standing;
        try
        {
            standing = StandingOf(reading);
        }
        catch (RequiredFactException)
        {
            return _ => false;
        }
        var owned = reading.Facts.Sleeping.Beds.Where(b => b.Owners.Count > 0).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        return target => TransitionBindingRules.SettledOpen(plan, target, causes, standing, owned.Contains);
    }

    /// <summary>The construction census by id as the settle rule reads it.</summary>
    private static Dictionary<string, StandingBuilding> StandingOf(ColonyReading reading)
    {
        var standing = new Dictionary<string, StandingBuilding>(StringComparer.Ordinal);
        foreach (var b in reading.Facts.CurrentConstruction.Buildings)
        {
            standing[b.Id] = new StandingBuilding(b.Observed.Building.DefName, [.. b.Cells]);
        }
        return standing;
    }

    /// <summary>
    /// Binds the transition decisions a committed step made into the recorded plan (task 8.2, TransitionBindingRules.WithTransitions):
    /// a chosen Relocate, Store or Remove is written once and read back by the next reviews instead of compared again, a decision
    /// to stay drops an earlier binding, and so does a removal gated on its replacement no longer serving (task 8.3: the dependency
    /// reopens, named on its row). It applies them to the newest recorded plan, so what another planner recorded earlier in
    /// this review (its own bindings, a bound defense) is kept and the decisions merge by target; <paramref name="held"/> is the
    /// caller's plan when none is recorded. Records nothing when the plan already says the same. Returns the plan, null when none.
    /// </summary>
    internal LayoutPlan? BindTransitions(GenerationSnapshot snapshot, Tick tick, LayoutPlan? held, IEnumerable<TransitionDecision> decisions, CancellationToken ct)
    {
        if ((Player.Journal.LayoutPlan(snapshot, tick, ct)?.Plan ?? held) is not { } plan)
        {
            return null;
        }
        var next = plan.WithTransitions(decisions);
        if (ReferenceEquals(next, plan))
        {
            return plan;
        }
        Player.Journal.RecordLayoutPlan(snapshot, tick, next, ct);
        // Each row names its dependency state: a bound removal's replacement serves; a dropped one gated on it reopened.
        var last = decisions.GroupBy(d => d.Target, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        foreach (var t in next.Transitions.Except(plan.Transitions))
        {
            Tel.Decide(TransitionDecisionOf("proposed", "transition_bound", t, last[t.Target].DependencyState));
        }
        foreach (var t in plan.Transitions.Where(t => last.GetValueOrDefault(t.Target) is { Gate: not null } && !next.Transitions.Any(n => n.Target == t.Target)))
        {
            Tel.Decide(TransitionDecisionOf("held", "transition_reopened", t, last[t.Target].DependencyState));
        }
        return next;
    }

    /// <summary><see cref="BindTransitions(GenerationSnapshot, Tick, LayoutPlan?, IEnumerable{TransitionDecision}, CancellationToken)"/> over a reading: the reading carries the recorded plan.</summary>
    internal ColonyReading BindTransitions(GenerationSnapshot snapshot, ColonyReading reading, IEnumerable<TransitionDecision> decisions, CancellationToken ct) =>
        BindTransitions(snapshot, reading.Identity.Tick, reading.Facts.LayoutPlan, decisions, ct) is { } plan && !ReferenceEquals(plan, reading.Facts.LayoutPlan)
            ? reading with { Facts = reading.Facts with { LayoutPlan = plan } }
            : reading;

    /// <summary>
    /// Records the plan without the transitions fresh facts show done or moot (TransitionBindingRules.Settled over the construction
    /// census and the sleeping census): never on a receipt. The paths that issued their removals derive the same settlement from
    /// the census (<see cref="SettledOf"/>) and cancel what is still unissued. The projection carries the recorded plan; unchanged
    /// while the census is unread.
    /// </summary>
    internal ColonyReading DropSettledTransitions(GenerationSnapshot snapshot, ColonyReading projection, CancellationToken ct)
    {
        if (projection.Facts.LayoutPlan is not { Transitions.Count: > 0 } plan)
        {
            return projection;
        }
        IReadOnlyList<string> settled;
        try
        {
            var owned = projection.Facts.Sleeping.Beds.Where(b => b.Owners.Count > 0).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            settled = TransitionBindingRules.Settled(plan, StandingOf(projection), owned.Contains);
        }
        catch (RequiredFactException)
        {
            return projection;
        }
        if (settled.Count == 0)
        {
            return projection;
        }
        var next = plan.WithoutTransitions(settled);
        Player.Journal.RecordLayoutPlan(snapshot, projection.Identity.Tick, next, ct);
        foreach (var t in plan.Transitions.Where(t => settled.Contains(t.Target)))
        {
            Tel.Decide(TransitionDecisionOf("closed", "transition_settled", t));
        }
        return projection with { Facts = projection.Facts with { LayoutPlan = next } };
    }

    /// <summary>
    /// Cancels the stale transition work among <paramref name="goal"/>'s open <paramref name="methods"/> (task 8.2): each unissued
    /// (pending or prepared) deconstruction or uninstall whose target the transition choice governs (<paramref name="governed"/>:
    /// decided in this review or settled on its census, <see cref="SettledOf"/>) and the plan carries no transition for
    /// (TransitionBindingRules.Stale): a choice re-selected to stay, gated on a replacement no longer serving, settled, or never
    /// planned in this timeline. Their claims release; orders Hands already issued finish,
    /// and nothing they report closes a transition. A cancelled removal is not issued again in the same load: each path's gate
    /// (MethodOnce, the latest attempt, the clearance step's withdrawn targets) holds it. Returns how many actions it cancelled.
    /// </summary>
    internal static int CancelStaleTransitionWork(JournalStore journal, NeedOwner goal, LayoutPlan plan, Func<MethodId, bool> methods, Func<string, bool> governed, string family, CancellationToken ct)
    {
        var open = new List<(NeedMethodBinding Method, ActionId Action, string Target)>();
        foreach (var m in goal.Methods.Where(m => methods(m.Method)))
        {
            // An unissued action is open work by itself: no census is needed to find it.
            foreach (var progress in journal.LoadPlan(m.Plan, ct).Progress.Where(p => p.View.Stage is Stage.Pending or Stage.Prepared))
            {
                if (RemovalTarget(progress.Action) is { } target && governed(target))
                {
                    open.Add((m, progress.View.Action, target));
                }
            }
        }
        var stale = TransitionBindingRules.Stale(plan, open, o => o.Target);
        foreach (var (m, action, _) in stale)
        {
            journal.Cancel(m.Plan, action, ct);
        }
        foreach (var (m, target) in stale.Select(s => (s.Method, s.Target)).Distinct())
        {
            Tel.Decide(new Decision
            {
                Kind = TelemetryKinds.LayoutEditKind,
                Component = "building",
                Verdict = "abandoned",
                Reason = "transition_stale",
                Target = target,
                Attrs = new Dictionary<string, object?> { ["family"] = family, ["owner"] = goal.OwnerKey, ["plan"] = m.Plan.Value },
            });
        }
        return stale.Count;
    }

    /// <summary>The targets of <paramref name="goal"/>'s unissued (pending or prepared) removals in <paramref name="methods"/>, in ordinal order.</summary>
    internal static IReadOnlyList<string> OpenRemovalTargets(JournalStore journal, NeedOwner goal, Func<MethodId, bool> methods, CancellationToken ct) =>
        [.. goal.Methods.Where(m => methods(m.Method))
            .SelectMany(m => journal.LoadPlan(m.Plan, ct).Progress.Where(p => p.View.Stage is Stage.Pending or Stage.Prepared))
            .Select(p => RemovalTarget(p.Action)).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The targets of <paramref name="goal"/>'s removals in <paramref name="methods"/> a stale cancellation withdrew in this load: none is proposed again.</summary>
    internal static IReadOnlySet<string> WithdrawnTargets(JournalStore journal, NeedOwner goal, Func<MethodId, bool> methods, CancellationToken ct)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in goal.Methods.Where(m => methods(m.Method)))
        {
            foreach (var progress in journal.LoadPlan(m.Plan, ct).Progress.Where(p => p.View.Stage == Stage.Cancelled))
            {
                if (RemovalTarget(progress.Action) is { } target)
                {
                    output.Add(target);
                }
            }
        }
        return output;
    }

    private static string? RemovalTarget(DomainAction action) => action.Deconstruction?.Target ?? action.UninstallBuilding?.Thing ?? action.MoveBuilding?.Thing;

    internal static Decision TransitionDecisionOf(string verdict, string reason, PlannedTransition t, string? dependency = null) => new()
    {
        Kind = TelemetryKinds.LayoutEditKind,
        Component = "building",
        Verdict = verdict,
        Reason = reason,
        Target = t.Target,
        Attrs = dependency is null
            ? new Dictionary<string, object?> { ["family"] = "building", ["kind"] = Wire.Name(t.Kind) }
            : new Dictionary<string, object?> { ["family"] = "building", ["kind"] = Wire.Name(t.Kind), ["dependency"] = dependency },
    };
}
