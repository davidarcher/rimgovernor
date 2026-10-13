using System.Diagnostics;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The review scope a planner wave runs in. Go carried these on the context (<c>immediateReviewKey</c>,
/// <c>ordinaryInterruptKey</c>); here they are ambient to the wave's async flow and restored on dispose.
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
    internal RoundsResult ImmediateStep(CancellationToken ct, CancellationToken epoch)
    {
        var began = Stopwatch.GetTimestamp();
        RoundsResult result = default;
        Exception? error = null;
        try
        {
            result = ImmediateStepCore(ct, epoch);
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
            d.Attrs["scope"] = "immediate";
            if (error is null && result.Review.Enabled)
            {
                d.Attrs["observation_tick"] = result.Review.Tick.Value;
            }

            TelemetryPackage.Decide(ct, d);
        }
    }

    private RoundsResult ImmediateStepCore(CancellationToken ctx, CancellationToken epoch)
    {
        var p = Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return p.StopRounds(ctx);
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("immediate review: observation unknown, snapshot invalid or native generation unset");
        }

        var previous = p.Journal.LoadRounds(ctx);
        Stage = Stage.Foothold;
        if (previous.Stage is not null)
        {
            Stage = previous.Stage.Stage;
        }

        var expected = StepScope(ctx, Native);
        if (expected.Colony != state.Snapshot.Colony || expected.Load != state.Snapshot.Load || expected.Map != state.Snapshot.Map
            || !expected.NativeGeneration.TryGetValue(out var native) || native != state.Snapshot.Native)
        {
            throw new ControlException("immediate review: the observed world is not the session's");
        }

        var reading = ObservationPackage.ObserveRoundsProtection(ctx, Native, Clock, expected, MaxAge);
        PublishFrame(expected, reading.Frame);
        var handed = p.ExpeditionHandoff(ctx, epoch, state, reading);
        if (handed)
        {
            return p.StopRounds(ctx);
        }

        var f = reading.Projection.Facts;
        var (layout, haveLayout) = LayoutPlanOf(ctx, state.Snapshot, expected.Tick);
        var projection = reading.Projection;
        if (haveLayout)
        {
            projection = projection with { LayoutPlan = Known(layout.Plan) };
            if (projection.Rooms.TryGetValue(out var rooms))
            {
                projection = projection with { Rooms = Known(PolicyFunctions.MarkEnemyDoors(rooms, layout.Plan.KillboxCells())) };
            }
        }

        if (MethodEnabled(ConcernId.MaintainShelter))
        {
            f = f with { SafeAreaOwed = SafeArea.Review(StockpileWorld(state.Snapshot), projection) };
        }

        f = f with { ShelterCombatants = ShelterCombatantsOf(ctx, p.Journal, PlayerWorld(state.Snapshot)) };
        var emergency = PolicyFunctions.NewEmergencySnapshot(state.Snapshot, expected.Tick, reading.Emergency);
        var (hostiles, critical) = PolicyFunctions.EmergencyNeeds(emergency, state.Snapshot, expected.Tick);
        var urgent = PolicyFunctions.UrgentPatients(emergency, state.Snapshot, expected.Tick);
        (critical, urgent) = PolicyFunctions.AmputationNeeds(emergency, f.MedicalPawns, critical, urgent);
        f = f with { Hostiles = hostiles, CriticalPatients = critical, UrgentPatients = urgent };
        f = f with { DangerSeeds = DangerSeedFact(f.Hostiles, reading.Emergency.Threats) };
        f.DangerSeeds.TryGetValue(out var seeds);
        f = f with { DangerWindow = SafeArea.DangerWindow(StockpileWorld(state.Snapshot), f.Hostiles, seeds, expected.Tick) };
        f = f with { DangerHaulers = PolicyFunctions.DangerHaulers(projection.WorkPawns) };
        f = f with { Tick = expected.Tick };
        f = f with { Dash = PolicyFunctions.ReviewDash(previous.Latches.Dash, f, projection.WorkPawns, expected.Tick, PolicyFunctions.PublishedHypothermiaTuning) };
        var needed = PlannedDrafts(ctx, p.Journal);
        var stray = IdleDrafts(state, reading.Emergency, reading.Frame.Pawns, needed);
        f = f with { CleanupPawns = Known(stray.Count > 0), AvailableMethods = Methods };
        projection = projection with { Facts = f };
        reading = reading with { Projection = projection };
        p.Current(ctx, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("immediate review authority changed");
        }

        Census.Retain(reading, true, Unknown<IReadOnlyList<ConstructionClaim>>());
        reading.Sections.File(Store, new FactsScope { Load = expected.Load.Value, Map = expected.Map.Value, Generation = native.Value });
        var result = p.Journal.ReviewRounds(ctx, new RoundsRequest
        {
            Revision = previous.Revision,
            Current = state.Snapshot,
            Tick = reading.Projection.Identity.Tick,
            Enabled = true,
            Immediate = true,
            Policy = Policy,
            Facts = f,
        });
        RecordRoundsSnapshot(ctx, state.Snapshot, reading.Projection.Identity.Tick, result, reading.Projection);
        return result;
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static bool ImmediatePlanner(PlannerEntry e)
    {
        switch (e.Name)
        {
            case "undraft":
            case DefensePlanner:
            case "tend":
            case "rescue":
            case "recovery":
            case "maintainShelter":
            case "fireSafety":
                return true;
        }

        return false;
    }

    /// <summary>Whether the running wave is the immediate review (Go <c>immediateReview(ctx)</c>; the flag rides the wave's async scope).</summary>
    internal static bool ImmediateReview() => ReviewScope.Immediate;

    internal static bool ProtectionNeedsTicks(ClockSchedulerResult result) =>
        (result.Defense is not null && (result.Defense.Plan.Value.Length != 0 || result.Defense.Verdict == BuildingReasonExistingWork))
        || (result.Tend is not null && result.Tend.NativeWorkTicks > 0)
        || (result.Rescue is not null && result.Rescue.NativeWorkTicks > 0)
        || (result.FireSafety is not null && result.FireSafety.NativeWorkTicks > 0)
        || PositiveFact(ClockShelterHeld(result.Rounds));
}

public sealed partial class ClockScheduler
{
    internal bool ImmediateConfigured()
    {
        var c = Config;
        return c.Rounds is not null && (c.Defense is not null || c.Tend is not null || c.Rescue is not null || c.Recovery is not null || c.MaintainShelter is not null || c.FireSafety is not null);
    }

    /// <summary>
    /// Runs the planner waves: the immediate (protection) planners first when any is due, then the ordinary
    /// ones unless a protection plan is still pending or native has work that needs ticks to advance.
    /// </summary>
    internal IReadOnlyList<string> RunPlanners(CancellationToken call, CancellationToken epoch, ClockSchedulerResult result, PlannerSelectionResult sel, Clock.Status status)
    {
        if (!ImmediateConfigured())
        {
            return RunPlannerWave(call, epoch, result, sel, status, false);
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
            names.AddRange(RunPlannerWave(call, epoch, result, urgent, status, true));
            Queue.All = false;
            foreach (var e in Queue.Entries())
            {
                if (Selected(e) && (!ImmediatePlanner(e) || e.Name == "recovery"))
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
        if (pending || (status.Running is null && (awaitNative || (immediate && ProtectionNeedsTicks(result)))))
        {
            result.Immediate = true;
            return names;
        }

        var ordinary = sel with { Began = Queue.Seq, Pick = e => Selected(e) && (!ImmediatePlanner(e) || e.Name == "recovery") };
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
            names.AddRange(RunPlannerWave(call, epoch, result, ordinary, status, false));
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
        foreach (var binding in r.Incidents)
        {
            if (!PolicyFunctions.ImmediateConcern(binding.Kind))
            {
                continue;
            }

            var state = Player.Journal.LoadIncident(ct, binding.Incident);
            foreach (var method in state.Methods)
            {
                plans.Add(method.Plan);
            }
        }

        foreach (var binding in r.Standards)
        {
            if (!PolicyFunctions.ImmediateConcern(binding.Concern))
            {
                continue;
            }

            var state = Player.Journal.LoadStandard(ct, binding.Standard);
            foreach (var method in state.Methods)
            {
                plans.Add(method.Plan);
            }
        }

        var actions = new List<ActionId>();
        var kinds = new HashSet<ActionKind>();
        foreach (var entry in Queue.Entries())
        {
            if (!ImmediatePlanner(entry))
            {
                continue;
            }

            foreach (var kind in entry.Kinds)
            {
                if (kind != ActionKind.RecoveryService)
                {
                    kinds.Add(kind);
                }
            }
        }

        // Plans are visited in id order so the result does not depend on set iteration.
        foreach (var id in plans.OrderBy(i => i))
        {
            var plan = Player.Journal.LoadPlan(ct, id);
            foreach (var p in plan.Progress)
            {
                var v = p.View;
                if (!plan.Retired && kinds.Contains(p.Action.Kind) && (v.Stage == Stage.Pending || v.Stage == Stage.Prepared))
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
                    if (!v.FreshHold(out _))
                    {
                        pending = true;
                    }
                }
                else if (v.Attempt != 0 && v.Stage != Stage.Cancelled && v.Stage != Stage.Unsuccessful)
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
