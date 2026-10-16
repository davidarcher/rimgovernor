namespace RimGovernor.Brain.BuildingRuntime;

// Port of the work census of internal/buildingruntime/clock_scheduler.go (clockSchedulerWork, roundsWork,
// runningWork, clockSchedulerCombatPlan, armedCombatStops). The run driver reads it after the planning step: the
// coupled orders ready under a running clock, the open fight and the fingerprint of the plans' actions. Whether a
// plan has work that needs game time is no longer asked: the clock runs whatever the plans hold.

internal static class ClockRunWork
{
    /// <summary>The fingerprint of <paramref name="plan"/>'s actions (Go <c>clockSchedulerWork</c>). A plan that is not the current revision is refused rather than guessed at.</summary>
    public static List<ClockWorkItem> Census(PlanState plan, GenerationSnapshot current)
    {
        if (plan.Spec.Id != current.Plan || plan.Spec.Revision != current.Revision || plan.Progress.Count != plan.Spec.Actions.Count)
        {
            throw new EvidenceException("clock work: plan is not the current revision");
        }
        var items = new List<ClockWorkItem>(plan.Progress.Count);
        foreach (var p in plan.Progress)
        {
            var v = p.View;
            items.Add(new ClockWorkItem(v.Action, p.Action.Kind, v.Stage, v.Attempt, v.Unresolved));
        }
        return items;
    }

    /// <summary>
    /// The dispatched or awaiting-observation watched actions of the root plan the running clock carries (<c>Live</c>)
    /// and the coupled orders ready under it (Go <c>runningWork</c>). A plan the census cannot read counts none.
    /// </summary>
    public static async Task<(int Live, IReadOnlyList<ActionId> Coupled)> RunningAsync(StoreJournal journal, GenerationSnapshot snapshot, Tick tick, CancellationToken ct)
    {
        var plan = await journal.LoadPlanAsync(snapshot.Plan, ct).ConfigureAwait(false);
        List<ClockWorkItem> items;
        try
        {
            items = Census(plan, snapshot);
        }
        catch (EvidenceException)
        {
            return (0, []);
        }
        var coupled = plan.Spec.CoupledPending([.. plan.Progress], snapshot, tick);
        var live = 0;
        foreach (var item in items)
        {
            if (item.Kind == ActionKind.Building && item.Attempt != default && item.Stage is Stage.Dispatched or Stage.AwaitingObservation)
            {
                live++;
            }
        }
        return (live, coupled);
    }

    /// <summary>
    /// Whether the current rounds files an active combat standing whose <c>military/combat</c> Need holds an admitted
    /// plan with open work (#3311): the only evidence under which live hostiles are watched rather than refused.
    /// <c>FightOpen</c> is whether a fight owns the combat and <c>Prey</c> the squad hunt's live prey (Go
    /// <c>clockSchedulerCombatPlan</c>).
    /// </summary>
    public static async Task<(bool Plan, bool FightOpen, EquatableArray<PawnId> Prey)> CombatPlanAsync(StoreJournal journal, GenerationSnapshot current, CancellationToken ct)
    {
        var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        if (!review.Enabled || review.Snapshot != current)
        {
            return (false, false, default);
        }
        var fights = await Task.Run(() => journal.OpenCombatFights(ct), ct).ConfigureAwait(false);
        if (review.Combat is not { Situation: Situation.Active } standing)
        {
            return (false, false, default);
        }
        var owner = await Task.Run(() => journal.LoadNeed(MilitaryFamily.Combat, current, ct), ct).ConfigureAwait(false);
        foreach (var method in owner.Methods)
        {
            // A fight owns the combat: its orders go out at each stop, not as plan work. Closed, it holds the
            // Need until its claims are released, with no stops armed. A squad hunt's fight is watched through
            // its live prey.
            if (fights.TryGetValue(method.Plan, out var fight))
            {
                return (true, fight.Open, fight.Open ? new EquatableArray<PawnId>(ImmutableArray.CreateRange(standing.Prey.Select(p => p))) : default);
            }
            var plan = await journal.LoadPlanAsync(method.Plan, ct).ConfigureAwait(false);
            if (Standard.WorkOpen(plan.Progress))
            {
                return (true, false, default);
            }
        }
        return (false, false, default);
    }

    /// <summary>The combat events a combat watch arms: the decision events while an admitted fight plan owns the combat, none otherwise (Go <c>armedCombatStops</c>).</summary>
    public static IReadOnlyList<PbClock.CombatEvent> ArmedStops(bool fightOpen) => fightOpen ? ClockCombatStops.DecisionEvents : [];
}
