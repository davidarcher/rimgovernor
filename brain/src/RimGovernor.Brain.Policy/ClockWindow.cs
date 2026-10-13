namespace RimGovernor.Brain.Policy;

// Go: clock_window.go

public static partial class PolicyFunctions
{
    /// <summary>
    /// Admits a finite window. Live hostiles refuse it unless the ActiveCombat goal holds an admitted plan;
    /// then the window is a combat watch acknowledging exactly those hostile pawns under the combat budget (a
    /// hostile building makes the window a combat one but is never acknowledged: the native watcher resolves
    /// acknowledged ids as pawns), and it returns to colony mode once every hostile is dead, downed or, for a
    /// building, destroyed. The returned snapshot and review revision must still match at runtime dispatch.
    /// </summary>
    public static ClockWindowDecision EvaluateClockWindow(ClockWindowFacts f, ClockWindowLimits limits)
    {
        var refused = new List<ClockWindowReason>();

        void Hold(ClockWindowReason reason)
        {
            if (!refused.Contains(reason))
            {
                refused.Add(reason);
            }
        }

        if (
            limits.Now == default
            || limits.MaxAge <= TimeSpan.Zero
            || limits.MaxTicks == 0
            || limits.MaxTicks > 1800000
            || limits.CombatMaxTicks > limits.MaxTicks
            || (f.Tick.Value >= 0 && f.Tick.Value > long.MaxValue - limits.MaxTicks)
        )
        {
            Hold(ClockWindowReason.InvalidLimits);
        }

        if (!f.Current.IsValid || f.Current.Revision.Value == 0 || f.Current.Native.Value == 0 || f.Tick.Value < 0)
        {
            Hold(ClockWindowReason.Unknown);
        }

        if (f.Status.Snapshot != f.Current || f.Status.Tick != f.Tick)
        {
            Hold(ClockWindowReason.Stale);
        }

        if (
            f.StartedAt == default
            || f.ObservedAt == default
            || f.ObservedAt < f.StartedAt
            || f.ObservedAt > limits.Now
            || f.StartedAt > limits.Now
            || limits.Now - f.StartedAt > limits.MaxAge
            || limits.Now - f.ObservedAt > limits.MaxAge
        )
        {
            Hold(ClockWindowReason.Stale);
        }

        var combatKnown = f.CombatPlan.TryGetValue(out var combatPlan);
        var unansweredKnown = f.SquadUnanswered.TryGetValue(out var unanswered);
        var shelteredKnown = f.Sheltered.TryGetValue(out var sheltered);
        sheltered = sheltered && shelteredKnown;
        var hostiles = new List<PawnId>();
        var combat = false;
        var buildings = HostileBuildingIDs(f.Emergency);
        var emergency = EvaluateEmergency(f.Emergency, f.Current, f.Tick);
        foreach (var h in emergency.Holds.Items)
        {
            switch (h.Reason)
            {
                case EmergencyHoldReason.StaleFacts:
                    Hold(ClockWindowReason.Stale);
                    break;
                case EmergencyHoldReason.UnknownFacts:
                    Hold(ClockWindowReason.Unknown);
                    break;
                case EmergencyHoldReason.UnsafeThreat:
                    // An admitted combat plan needs ticks for defenders to fight; the native watcher only lets
                    // them pass with every live hostile acknowledged. Without a plan a raid must not
                    // auto-advance.
                    var named = h.Pawn.Value.Length != 0;
                    if (!combatKnown)
                    {
                        Hold(ClockWindowReason.Unknown);
                    }
                    else if (combatPlan && named && buildings.Contains(h.Pawn))
                    {
                        // A hostile building makes the window a combat one but is never acknowledged: the
                        // native watcher stops for unacknowledged hostile pawns only and resolves every
                        // acknowledged id as a pawn.
                        combat = true;
                    }
                    else if (named && buildings.Contains(h.Pawn) && unansweredKnown && unanswered)
                    {
                        // No squad can be assigned (every colonist downed or incapable of violence): the
                        // building goes nowhere, so it is watched rather than held. The planner reports again
                        // at every stop and a plan it admits later makes the next window a combat one.
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
                        Hold(ClockWindowReason.Unsafe);
                    }

                    break;
                case EmergencyHoldReason.CriticalMedical:
                    // Must NOT refuse the window here: RoundsTendPlanner dispatches the tend order regardless
                    // of window admission, but the native side can only carry it out -- and NeedsTend can only
                    // clear -- while ticks are actually passing. Refusing to admit a window while NeedsTend is
                    // true deadlocks: the order times out unexecuted, the reviewer retries next poll, and the
                    // colony is unsafe forever. Unlike a hostile threat, an untended patient is resolved BY
                    // letting the clock run, not by holding it.
                    break;
                default:
                    Hold(ClockWindowReason.Unsafe);
                    break;
            }
        }

        if (f.Emergency.PodsPending())
        {
            // A drop-pod raid on its way down: with the fight's plan the window is a combat one with nothing
            // to acknowledge until the pods open (the watcher stops on the raiders then); without it the raid
            // must not auto-advance.
            if (!combatKnown)
            {
                Hold(ClockWindowReason.Unknown);
            }
            else if (combatPlan)
            {
                combat = true;
            }
            else
            {
                Hold(ClockWindowReason.Unsafe);
            }
        }

        if (combatPlan && f.HuntPrey.Count > 0)
        {
            // A hunt fight has no hostile to make the window a combat one, yet its decisions wait on combat
            // stops: the prey are the watched ids.
            combat = true;
            hostiles.AddRange(f.HuntPrey.Items);
        }

        void Check(Fact<bool> fact, bool want, ClockWindowReason reason)
        {
            if (!fact.TryGetValue(out var value))
            {
                Hold(ClockWindowReason.Unknown);
            }
            else if (value != want)
            {
                Hold(reason);
            }
        }

        Check(f.Obligations.Complete, true, ClockWindowReason.Unknown);
        Check(f.Obligations.OwnedEpochPending, false, ClockWindowReason.Outstanding);
        Check(f.Obligations.UnknownStartPending, false, ClockWindowReason.Outstanding);
        Check(f.WorkRemaining, true, ClockWindowReason.NoWork);
        switch (f.Status.State)
        {
            case ClockWindowState.NeverStarted:
            case ClockWindowState.Stopped:
                break;
            case ClockWindowState.Running:
            case ClockWindowState.Stopping:
                Hold(ClockWindowReason.NotPaused);
                break;
            default:
                Hold(ClockWindowReason.Unknown);
                break;
        }

        Check(f.Status.ActualPaused, true, ClockWindowReason.NotPaused);
        Check(f.Status.NativeTickBoundary, true, ClockWindowReason.Unknown);
        var known = f.Status.DurableEvents.TryGetValue(out var durable);
        if (!known || (!durable && f.Status.State != ClockWindowState.NeverStarted))
        {
            Hold(ClockWindowReason.Unknown);
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
            Hold(ClockWindowReason.Unreviewed);
        }

        if (!f.Status.NewestCursor.TryGetValue(out var newest))
        {
            Hold(ClockWindowReason.Unknown);
        }
        else if (newest < 0 || newest != review.Captured)
        {
            Hold(ClockWindowReason.Unreviewed);
        }

        Check(review.HasHolds, false, ClockWindowReason.Interrupted);
        if (refused.Count != 0)
        {
            return new ClockWindowDecision { Refused = EquatableArray.From(refused) };
        }

        var result = new ClockWindowDecision
        {
            Admitted = true,
            Refused = EquatableArray.From(refused),
            Snapshot = f.Current,
            Tick = f.Tick,
            ReviewRevision = review.Revision,
            CapturedCursor = review.Captured,
            MaxTicks = limits.MaxTicks,
            Mode = ClockWindowMode.Colony,
            Downed = EquatableArray.From(KnownDownedColonists(f.Emergency)),
        };
        if (hostiles.Count > 0 || combat)
        {
            result = result with
            {
                Mode = ClockWindowMode.Combat,
                Hostiles = EquatableArray.From(hostiles.OrderBy(h => h.Value, StringComparer.Ordinal)),
            };
            if (limits.CombatMaxTicks != 0)
            {
                result = result with { MaxTicks = limits.CombatMaxTicks };
            }
        }

        return result;
    }

    /// <summary>The set of HostileBuilding rows in the census.</summary>
    private static HashSet<PawnId> HostileBuildingIDs(EmergencySnapshot snapshot)
    {
        var output = new HashSet<PawnId>();
        foreach (var t in snapshot.Facts.Threats.Items)
        {
            if (t.Building())
            {
                output.Add(t.ID);
            }
        }

        return output;
    }

    /// <summary>
    /// Lists, sorted, the living colonists the emergency census knows to be downed. The window acknowledges
    /// them so the native watcher lets ticks pass for their rescue instead of stopping at once on a casualty
    /// the service already holds a CriticalMedical goal for.
    /// </summary>
    private static List<PawnId> KnownDownedColonists(EmergencySnapshot snapshot)
    {
        var downed = new List<PawnId>();
        foreach (var pawn in snapshot.Facts.Colonists.Items)
        {
            var deadKnown = pawn.Dead.TryGetValue(out var dead);
            var downKnown = pawn.Downed.TryGetValue(out var down);
            if (deadKnown && !dead && downKnown && down)
            {
                downed.Add(pawn.ID);
            }
        }

        return [.. downed.OrderBy(p => p.Value, StringComparer.Ordinal)];
    }
}
