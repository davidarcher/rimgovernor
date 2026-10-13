namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_window.go.
public sealed partial class ClockCoordinator
{
    /// <summary>
    /// Carries the same captured facts through queueing and dispatch (Go <c>CommandWindow</c>). Revalidation never
    /// refreshes their timestamps or grants replacement authority.
    /// </summary>
    public Task<(ClockAttempt? Attempt, Exception? Error)> CommandWindow(ClockWindowRequest request, CancellationToken ct)
    {
        var intent = request.Intent;
        if (intent.Window is null || intent.Command.Start is null || intent.Command.Renew is not null || intent.Command.Speed is not null)
        {
            return Held();
        }

        // Either mode acknowledges only the downed colonists the window decision names below; combat mode also its
        // hostile pawns (none when the fight is against a hostile building alone). Medical suppression never.
        var p = intent.Command.Start.Policy;
        if (p is null || p.AcknowledgedInjuredColonistIds.Count + p.SurgicalRecoveryIds.Count + p.MedicalRestIds.Count != 0 || p.InjuryStopCooldownMs.GetValueOrDefault() != 0)
        {
            return Held();
        }
        switch (p.Mode.GetValueOrDefault())
        {
            case PbClock.WatchMode.Colony:
                if (p.AcknowledgedHostileIds.Count != 0)
                {
                    return Held();
                }
                break;
            case PbClock.WatchMode.Combat:
                break;
            default:
                return Held();
        }

        // The start is copied so the revalidation below reads the policy this command was admitted with.
        var start = intent.Command.Start with { Policy = p };
        intent = intent with { Command = intent.Command with { Start = start } };

        void Check(ClockIntent value)
        {
            if (value.Window is null || value.Command.Start is null)
            {
                throw new HeldException();
            }
            var decision = PolicyApi.EvaluateClockWindow(
                request.Facts,
                new ClockWindowLimits { Now = _clock.Now(), MaxAge = request.MaxAge, MaxTicks = value.Command.Start.MaxTicks, CombatMaxTicks = request.CombatMaxTicks });
            var w = value.Window;
            if (!decision.Admitted || value.Snapshot != decision.Snapshot || w.Snapshot != decision.Snapshot || w.Tick != decision.Tick
                || w.ReviewRevision != decision.ReviewRevision || w.CapturedCursor != decision.CapturedCursor || w.MaxTicks != decision.MaxTicks)
            {
                throw new HeldException();
            }
            ClockWindowPolicyMatches(value.Command.Start.Policy, decision);
        }

        // The admitting step's own status stands in for the pre-dispatch clock status read while it is within
        // MaxAge; the check above still re-evaluates the same facts against the wall clock.
        PbClock.Status? Observed()
        {
            if (request.Status is null || request.StatusAt == default || _clock.Now() - request.StatusAt > request.MaxAge)
            {
                return null;
            }
            return request.Status;
        }

        return CommandAsync(intent, Check, Observed, ct);

        static Task<(ClockAttempt? Attempt, Exception? Error)> Held() =>
            Task.FromResult<(ClockAttempt?, Exception?)>((null, new HeldException()));
    }

    /// <summary>
    /// Holds unless the start policy watches in the mode the window decision admitted and acknowledges exactly its
    /// hostiles and its known downed colonists (Go <c>clockWindowPolicyMatches</c>).
    /// </summary>
    internal static void ClockWindowPolicyMatches(PbClock.WatchPolicy? p, ClockWindowDecision decision)
    {
        PbClock.WatchMode mode;
        switch (decision.Mode)
        {
            case ClockWindowMode.Colony:
                mode = PbClock.WatchMode.Colony;
                break;
            case ClockWindowMode.Combat:
                mode = PbClock.WatchMode.Combat;
                break;
            default:
                throw new HeldException();
        }
        if (p is null || p.Mode.GetValueOrDefault() != mode || p.AcknowledgedHostileIds.Count != decision.Hostiles.Count)
        {
            throw new HeldException();
        }
        for (var i = 0; i < decision.Hostiles.Count; i++)
        {
            if (p.AcknowledgedHostileIds[i] != decision.Hostiles[i].Value)
            {
                throw new HeldException();
            }
        }
        if (p.AcknowledgedDownedColonistIds.Count != decision.Downed.Count)
        {
            throw new HeldException();
        }
        for (var i = 0; i < decision.Downed.Count; i++)
        {
            if (p.AcknowledgedDownedColonistIds[i] != decision.Downed[i].Value)
            {
                throw new HeldException();
            }
        }
    }
}
