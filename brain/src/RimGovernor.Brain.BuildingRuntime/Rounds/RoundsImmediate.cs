using System.Diagnostics;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The review scope a planner wave runs in. They are ambient to the wave's async flow and restored on dispose.
/// </summary>
internal static class ReviewScope
{
    private static readonly AsyncLocal<bool> ImmediateValue = new();

    private static readonly AsyncLocal<CancellationToken?> OrdinaryInterruptValue = new();

    /// <summary>Whether the running wave is the immediate (protection) review.</summary>
    internal static bool Immediate => ImmediateValue.Value;

    /// <summary>The ordinary wave's token the immediate wave may interrupt, when one is running.</summary>
    internal static CancellationToken? OrdinaryInterrupt => OrdinaryInterruptValue.Value;

    internal static IDisposable BeginImmediate(bool immediate)
    {
        var previous = ImmediateValue.Value;
        ImmediateValue.Value = immediate;
        return new Restore(() => ImmediateValue.Value = previous);
    }

    internal static IDisposable BeginOrdinaryInterrupt(CancellationToken? ordinary)
    {
        var previous = OrdinaryInterruptValue.Value;
        OrdinaryInterruptValue.Value = ordinary;
        return new Restore(() => OrdinaryInterruptValue.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

public sealed partial class Rounder
{
    /// <summary>
    /// The immediate (protection) review: observes only what the protection planners need, files the frame,
    /// and reviews rounds with the immediate flag so ordinary concerns wait for the full review.
    /// </summary>
    internal async Task<RoundsResult> ImmediateStepAsync(CancellationToken ct, CancellationToken epoch)
    {
        var began = Stopwatch.GetTimestamp();
        RoundsResult? result = null;
        Exception? error = null;
        try
        {
            result = await ImmediateStepCoreAsync(ct, epoch).ConfigureAwait(false);
            return result;
        }
        catch (Exception e)
        {
            error = e;
            throw;
        }
        finally
        {
            var d = RoundsStepDecision(error, Stopwatch.GetElapsedTime(began), true);
            var attrs = new Dictionary<string, object?>(d.Attrs) { ["scope"] = "immediate" };
            if (error is null && result is not null && result.Review.Enabled)
            {
                attrs["observation_tick"] = result.Review.Tick.Value;
            }

            Tel.Decide(d with { Attrs = attrs });
        }
    }

    private async Task<RoundsResult> ImmediateStepCoreAsync(CancellationToken ctx, CancellationToken epoch)
    {
        var p = Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return await p.StopRoundsAsync(ctx).ConfigureAwait(false);
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("immediate review: observation unknown, snapshot invalid or native generation unset");
        }

        var previous = p.Journal.LoadRounds(ctx);
        var expected = await StepScope.StepScopeAsync(Native, ctx).ConfigureAwait(false);
        if (expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.Snapshot.Map
            || !expected.NativeGeneration.TryGetValue(out var native) || native != state.Snapshot.Native)
        {
            throw new ControlException("immediate review: the observed world is not the session's");
        }

        var reading = await RoundsReads.ObserveRoundsProtection(Native, Clock, expected, MaxAge, ctx).ConfigureAwait(false);
        PublishFrame(reading.Frame);
        var f = reading.Projection.Facts;
        var layout = await p.Journal.LayoutPlanAsync(state.Snapshot, expected.Tick, ctx).ConfigureAwait(false);
        if (layout is { Invalid: true } && !LayoutInvalidLogged)
        {
            LayoutInvalidLogged = true;
        }

        var projection = reading.Projection;
        if (layout is { Plan: { } layoutPlan })
        {
            projection = projection with { LayoutPlan = Known(layoutPlan) };
            if (projection.Rooms.TryGetValue(out var rooms))
            {
                projection = projection with { Rooms = Known(RoomsRules.MarkEnemyDoors(rooms, layoutPlan.KillboxCells())) };
            }
        }

        if (Needs.Contains(ShelterFamily.SafeArea))
        {
            f = f with { SafeAreaOwed = SafeArea.Review(RoundsStockpilePlanner.StockpileWorld(state.Snapshot), projection) };
        }

        var emergency = EmergencySnapshot.New(state.Snapshot, expected.Tick, reading.Emergency);
        var (hostiles, critical) = EmergencyNeedsRules.EmergencyNeeds(emergency, state.Snapshot, expected.Tick);
        var urgent = EmergencyNeedsRules.UrgentPatients(emergency, state.Snapshot, expected.Tick);
        (critical, urgent) = EmergencyNeedsRules.AmputationNeeds(emergency, f.MedicalPawns, critical, urgent);
        f = f with { Hostiles = hostiles, CriticalPatients = critical, UrgentPatients = urgent };
        f = f with { DangerSeeds = SafeAreaMemory.DangerSeedFact(f.Hostiles, reading.Emergency.Threats) };
        f.DangerSeeds.TryGetValue(out var seeds);
        f = f with { DangerWindow = SafeArea.DangerWindow(RoundsStockpilePlanner.StockpileWorld(state.Snapshot), f.Hostiles, seeds, expected.Tick) };
        f = f with { DangerHaulers = ShelterRules.DangerHaulers(projection.WorkPawns) };
        f = f with { Tick = expected.Tick };
        f = f with { Dash = DashPlanRules.ReviewDash(previous.Latches.Dash, f, projection.WorkPawns, expected.Tick, DashEstimateRules.PublishedHypothermiaTuning) };
        f = f with { AvailableNeeds = AvailableNeeds };
        // The immediate pass plans no food; the last full review's plan carries forward, so a squad hunt it opened
        // stays a combat deficit (#3311).
        f = f with { FoodPlan = HeldFoodPlan(state.Snapshot) };
        projection = projection with { Facts = f };
        reading = reading with { Reading = reading.Reading with { Projection = projection } };
        p.Current(ctx, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("immediate review authority changed");
        }

        Census.Retain(reading, true, Unknown<EquatableArray<ConstructionClaim>>());
        reading.Sections.File(Store, new FactScope { Load = expected.Load.Value, Map = expected.Map.Value, Generation = native.Value });
        var result = p.Journal.ReviewRounds(new RoundsRequest
        {
            Revision = previous.Revision,
            Current = state.Snapshot,
            Tick = reading.Projection.Identity.Tick,
            Enabled = true,
            Immediate = true,
            Policy = Policy,
            Facts = f,
        }, ctx);
        RecordRoundsSnapshot(state.Snapshot, reading.Projection.Identity.Tick, result, reading.Projection, ctx);
        return result;
    }
}

partial class ClockScheduler
{
    internal static bool ImmediatePlanner(PlannerEntry e) => e.Immediate;

    /// <summary>
    /// Whether the ordinary wave runs the entry: every non-immediate planner, and every family, whose immediate run
    /// judged only its SeriousHarm Needs on the narrow read (#3250).
    /// </summary>
    internal static bool OrdinaryPlanner(PlannerEntry e) => !ImmediatePlanner(e) || e.Family is not null;

    /// <summary>Whether the running wave is the immediate review.</summary>
    internal static bool ImmediateReview() => ReviewScope.Immediate;

    internal static bool ProtectionNeedsTicks(ClockSchedulerResult result) =>
        (result.Defense is not null && (result.Defense.Plan.Value.Length != 0 || result.Defense.Verdict == BuildingReasonExistingWork))
        || RoundsBuildingPlanner.PositiveFact(ClockShelterHeld(result.Rounds));
}

public sealed partial class ClockScheduler
{
    internal bool ImmediateConfigured()
    {
        var c = Config;
        return c.Rounds is not null && (c.Defense is not null || c.Families.Any(f => f.Immediate));
    }

    /// <summary>
    /// Runs the planner waves: the immediate (protection) planners first when any is due, then the ordinary
    /// ones unless a protection plan is still pending or native has work that needs ticks to advance.
    /// </summary>
    internal async Task<IReadOnlyList<string>> RunPlannersAsync(ClockSchedulerResult result, PlannerSelectionResult sel, K.Status status, CancellationToken call, CancellationToken epoch)
    {
        if (!ImmediateConfigured())
        {
            return await RunPlannerWaveAsync(result, sel, (status.Context?.Tick).GetValueOrDefault(), false, "all", call, epoch).ConfigureAwait(false);
        }

        bool Selected(PlannerEntry e) => sel.Pick is null || sel.Pick(e);
        var immediate = false;
        foreach (var e in Queue.Entries())
        {
            immediate = immediate || (Selected(e) && ImmediatePlanner(e));
        }

        var names = new List<string>();
        if (immediate)
        {
            var urgent = sel with { Pick = e => Selected(e) && ImmediatePlanner(e) };
            names.AddRange(await RunPlannerWaveAsync(result, urgent, (status.Context?.Tick).GetValueOrDefault(), true, null, call, epoch).ConfigureAwait(false));
            Queue.All = false;
            foreach (var e in Queue.Entries())
            {
                if (Selected(e) && OrdinaryPlanner(e))
                {
                    Queue.Mark(e.Name);
                }
            }

            foreach (var id in ImmediateActions(call))
            {
                if (!Protection.Contains(id))
                {
                    Protection.Add(id);
                }
            }

            result.Immediate = true;
            if (result.HeldBy.Count > 0)
            {
                return names;
            }
        }

        var (pending, awaitNative) = ProtectionPending(call);
        if (pending || (status.State != PbClock.ClockState.Running && (awaitNative || (immediate && ProtectionNeedsTicks(result)))))
        {
            result.Immediate = true;
            return names;
        }

        var ordinary = sel with { Began = Queue.Seq, Pick = e => Selected(e) && OrdinaryPlanner(e) };
        var hasOrdinary = false;
        foreach (var e in Queue.Entries())
        {
            hasOrdinary = hasOrdinary || ordinary.Pick!(e);
        }

        if (!hasOrdinary)
        {
            return names;
        }

        result.Immediate = false;
        try
        {
            names.AddRange(await RunPlannerWaveAsync(result, ordinary, (status.Context?.Tick).GetValueOrDefault(), false, "all", call, epoch).ConfigureAwait(false));
        }
        catch (OperationCanceledException e) when (!call.IsCancellationRequested)
        {
            throw new ControlException("the ordinary wave was interrupted", e);
        }

        return names;
    }

    /// <summary>The ids of the pending protection actions the immediate planners own, sorted.</summary>
    internal IReadOnlyList<ActionId> ImmediateActions(CancellationToken ct)
    {
        var r = Player.Journal.LoadRounds(ct);
        var plans = new HashSet<PlanId>();
        // The fight's plans while the review files a combat standing (#3311), as the combat incident's were.
        if (r.Combat is not null)
        {
            foreach (var method in Player.Journal.LoadNeed(MilitaryFamily.Combat, r.Snapshot, ct).Methods)
            {
                plans.Add(method.Plan);
            }
        }

        var actions = new List<ActionId>();
        var kinds = new HashSet<ActionKind>();
        // A family's SeriousHarm Needs are its protection: the plans its latest immediate run saw open, watched for
        // the family's own kinds beside the planners'. Its kinds do not widen the watch on the planners' plans.
        var familyKinds = new Dictionary<PlanId, HashSet<ActionKind>>();
        foreach (var entry in Queue.Entries())
        {
            if (!ImmediatePlanner(entry))
            {
                continue;
            }

            if (entry.Family is { } family)
            {
                foreach (var plan in family.SeriousHarmPlans)
                {
                    if (!familyKinds.TryGetValue(plan, out var own))
                    {
                        familyKinds[plan] = own = [];
                    }
                    own.UnionWith(entry.Kinds.Where(k => k != ActionKind.RecoveryService));
                }
                continue;
            }

            foreach (var kind in entry.Family?.Schedule.ProtectionKinds ?? entry.Kinds)
            {
                if (kind != ActionKind.RecoveryService)
                {
                    kinds.Add(kind);
                }
            }
        }
        plans.UnionWith(familyKinds.Keys);

        // Plans are visited in id order so the result does not depend on set iteration.
        foreach (var id in plans.OrderBy(i => i))
        {
            var plan = Player.Journal.LoadPlan(id, ct);
            HashSet<ActionKind> watched = familyKinds.TryGetValue(id, out var own) ? [.. kinds, .. own] : kinds;
            foreach (var p in plan.Progress)
            {
                var v = p.View;
                if (!plan.Retired && watched.Contains(p.Action.Kind) && (v.Stage == Stage.Pending || v.Stage == Stage.Prepared))
                {
                    actions.Add(p.View.Action);
                }
            }
        }

        actions.Sort();
        return actions;
    }

    /// <summary>
    /// Whether a watched protection action is still waiting to be sent (pending) or has been attempted and
    /// is in flight (advance, so the worker needs ticks); the watch list clears once nothing is pending.
    /// </summary>
    internal (bool Pending, bool Advance) ProtectionPending(CancellationToken ct)
    {
        if (!Config.Worker || Protection.Count == 0)
        {
            return (false, false);
        }

        var plans = Player.Journal.LoadPlans(ct);
        var watched = new HashSet<ActionId>(Protection);
        var pending = false;
        var advance = false;
        foreach (var plan in plans)
        {
            if (plan.Retired)
            {
                continue;
            }

            foreach (var p in plan.Progress)
            {
                var v = p.View;
                if (!watched.Contains(v.Action))
                {
                    continue;
                }

                if (v.Stage == Stage.Pending || v.Stage == Stage.Prepared)
                {
                    if (!v.TryGetFreshHold(out _))
                    {
                        pending = true;
                    }
                }
                else if (v.Attempt.Value != 0 && v.Stage != Stage.Cancelled && v.Stage != Stage.Unsuccessful)
                {
                    advance = true;
                }
            }
        }

        if (!pending)
        {
            Protection.Clear();
        }

        return (pending, advance);
    }
}
