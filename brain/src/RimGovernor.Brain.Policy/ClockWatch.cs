namespace RimGovernor.Brain.Policy;

// Go: clock_window.go, reduced to the continuous Run/Hold clock.

public static partial class PolicyFunctions
{
    /// <summary>
    /// Decides the watch a Run carries. Live hostiles refuse it unless the ActiveCombat goal holds an admitted
    /// plan; then the watch is a combat one acknowledging exactly those hostile pawns (a hostile building makes
    /// the watch a combat one but is never acknowledged: the native watcher resolves acknowledged ids as pawns),
    /// and it returns to colony mode once every hostile is dead, downed or, for a building, destroyed. A refused
    /// decision leaves a held clock held; it never stops a running one (native holds on what it sees). The
    /// decision is pure: the caller diffs the policy it implies against the last one it sent.
    /// </summary>
    public static ClockWatchDecision EvaluateClockWatch(ClockWatchFacts f, ClockWatchLimits limits)
    {
        var refused = new List<ClockWatchReason>();

        void Hold(ClockWatchReason reason)
        {
            if (!refused.Contains(reason))
            {
                refused.Add(reason);
            }
        }

        if (limits.MaxAge <= TimeSpan.Zero)
        {
            Hold(ClockWatchReason.Unknown);
        }

        if (!f.Current.IsValid || f.Current.Revision.Value == 0 || f.Current.Native.Value == 0 || f.Tick.Value < 0)
        {
            Hold(ClockWatchReason.Unknown);
        }

        if (f.Status.Snapshot != f.Current || f.Status.Tick != f.Tick)
        {
            Hold(ClockWatchReason.Stale);
        }

        if (
            f.SinceStarted < TimeSpan.Zero
            || f.SinceObserved < TimeSpan.Zero
            || f.SinceObserved > f.SinceStarted
            || f.SinceStarted > limits.MaxAge
        )
        {
            Hold(ClockWatchReason.Stale);
        }

        var combatKnown = f.CombatPlan.TryGetValue(out var combatPlan);
        var unansweredKnown = f.SquadUnanswered.TryGetValue(out var unanswered);
        var shelteredKnown = f.Sheltered.TryGetValue(out var sheltered);
        sheltered = sheltered && shelteredKnown;
        var hostiles = new List<PawnId>();
        var combat = false;
        var buildings = HostileBuildingIDs(f.Emergency);
        var emergency = EvaluateEmergency(f.Emergency, f.Current, f.Tick);
        foreach (var h in emergency.Holds)
        {
            switch (h.Reason)
            {
                case EmergencyReason.StaleFacts:
                    Hold(ClockWatchReason.Stale);
                    break;
                case EmergencyReason.UnknownFacts:
                    Hold(ClockWatchReason.Unknown);
                    break;
                case EmergencyReason.UnsafeThreat:
                    // An admitted combat plan needs ticks for defenders to fight; the native watcher only lets
                    // them pass with every live hostile acknowledged. Without a plan a raid must not
                    // auto-advance.
                    var named = h.Pawn.Value.Length != 0;
                    if (!combatKnown)
                    {
                        Hold(ClockWatchReason.Unknown);
                    }
                    else if (combatPlan && named && buildings.Contains(h.Pawn))
                    {
                        // A hostile building makes the watch a combat one but is never acknowledged: the
                        // native watcher holds for unacknowledged hostile pawns only and resolves every
                        // acknowledged id as a pawn.
                        combat = true;
                    }
                    else if (named && buildings.Contains(h.Pawn) && unansweredKnown && unanswered)
                    {
                        // No squad can be assigned (every colonist downed or incapable of violence): the
                        // building goes nowhere, so it is watched rather than held. The planner reports again
                        // at every hold and a plan it admits later makes the next watch a combat one.
                    }
                    else if (combatPlan && named)
                    {
                        hostiles.Add(h.Pawn);
                    }
                    else if (sheltered && named && !buildings.Contains(h.Pawn))
                    {
                        // Every undrafted colonist is in the Safe area: the threat is waited out, watched like
                        // a fight's.
                        hostiles.Add(h.Pawn);
                    }
                    else
                    {
                        Hold(ClockWatchReason.Unsafe);
                    }

                    break;
                case EmergencyReason.CriticalMedical:
                    // Must NOT refuse the Run here: RoundsTendPlanner dispatches the tend order regardless of
                    // the clock, but the native side can only carry it out -- and NeedsTend can only clear --
                    // while ticks are actually passing. Refusing to run while NeedsTend is true deadlocks: the
                    // order times out unexecuted, the reviewer retries, and the colony is unsafe forever. Unlike a
                    // hostile threat, an untended patient is resolved BY letting the clock run, not by holding it.
                    break;
                default:
                    Hold(ClockWatchReason.Unsafe);
                    break;
            }
        }

        if (f.Emergency.PodsPending)
        {
            // A drop-pod raid on its way down: with the fight's plan the watch is a combat one with nothing
            // to acknowledge until the pods open (the watcher holds on the raiders then); without it the raid
            // must not auto-advance.
            if (!combatKnown)
            {
                Hold(ClockWatchReason.Unknown);
            }
            else if (combatPlan)
            {
                combat = true;
            }
            else
            {
                Hold(ClockWatchReason.Unsafe);
            }
        }

        if (combatPlan && f.HuntPrey.Count > 0)
        {
            // A hunt fight has no hostile to make the watch a combat one, yet its decisions wait on combat
            // holds: the prey are the watched ids.
            combat = true;
            hostiles.AddRange(f.HuntPrey.Items);
        }

        void Check(Fact<bool> fact, bool want, ClockWatchReason reason)
        {
            if (!fact.TryGetValue(out var value))
            {
                Hold(ClockWatchReason.Unknown);
            }
            else if (value != want)
            {
                Hold(reason);
            }
        }

        if (f.Status.State is not (ClockWatchState.Running or ClockWatchState.Held))
        {
            Hold(ClockWatchReason.Unknown);
        }

        Check(f.Status.NativeTickBoundary, true, ClockWatchReason.Unknown);
        var known = f.Status.DurableEvents.TryGetValue(out var durable);
        if (!known || (!durable && !f.Status.NeverRun))
        {
            Hold(ClockWatchReason.Unknown);
        }

        var review = f.Review;
        if (
            review.Captured < 0
            || review.Reviewed < 0
            || review.Acknowledged < 0
            || review.Acknowledged > review.Reviewed
            || review.Reviewed != review.Captured
        )
        {
            Hold(ClockWatchReason.Unreviewed);
        }

        if (!f.Status.NewestCursor.TryGetValue(out var newest))
        {
            Hold(ClockWatchReason.Unknown);
        }
        else if (newest < 0 || (f.Status.State == ClockWatchState.Held && newest > review.Captured))
        {
            // A held clock resumes only once the brain has captured the events up to the hold; a running one
            // keeps capturing, so its cursor runs ahead of the review by design.
            Hold(ClockWatchReason.Unreviewed);
        }

        Check(review.HasHolds, false, ClockWatchReason.Interrupted);
        if (refused.Count != 0)
        {
            return new ClockWatchDecision { Refused = EquatableArray.From(refused) };
        }

        var result = new ClockWatchDecision
        {
            Run = true,
            Refused = EquatableArray.From(refused),
            Snapshot = f.Current,
            Tick = f.Tick,
            Mode = ClockWatchMode.Colony,
            Downed = EquatableArray.From(KnownDownedPatients(f.Emergency)),
        };
        if (hostiles.Count > 0 || combat)
        {
            result = result with
            {
                Mode = ClockWatchMode.Combat,
                Hostiles = EquatableArray.From(hostiles.OrderBy(h => h.Value, StringComparer.Ordinal)),
            };
        }

        return result;
    }

    /// <summary>The set of HostileBuilding rows in the census.</summary>
    private static HashSet<PawnId> HostileBuildingIDs(EmergencySnapshot snapshot)
    {
        var output = new HashSet<PawnId>();
        foreach (var t in snapshot.Facts.Threats)
        {
            if (t.Building)
            {
                output.Add(t.Id);
            }
        }

        return output;
    }

    /// <summary>
    /// Lists, sorted, the living colonists the emergency census knows to be downed. The watch acknowledges
    /// them so the native watcher lets ticks pass for their rescue instead of holding at once on a casualty
    /// the service already holds a CriticalMedical goal for.
    /// </summary>
    private static List<PawnId> KnownDownedPatients(EmergencySnapshot snapshot)
    {
        var downed = new List<PawnId>();
        foreach (var pawn in EmergencyPatients(snapshot.Facts))
        {
            var deadKnown = pawn.Dead.TryGetValue(out var dead);
            var downKnown = pawn.Downed.TryGetValue(out var down);
            if (deadKnown && !dead && downKnown && down)
            {
                downed.Add(pawn.Id);
            }
        }

        return [.. downed.OrderBy(p => p.Value, StringComparer.Ordinal)];
    }
}
