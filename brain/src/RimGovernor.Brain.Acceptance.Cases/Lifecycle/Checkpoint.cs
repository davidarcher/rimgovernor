using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/checkpoint</c>: the trusted native save checkpoint. A paused save completes and echoes what it was
/// asked; a save while the game runs is refused; a stale expected tick is uncertain; <c>lifecycle_read_save</c> replays
/// the completed outcome by request id and refuses an unknown one.
/// </summary>
internal static class LifecycleCheckpoint
{
    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/checkpoint",
        Scope = "lifecycle_save on the lab: a paused save completes echoing identity, tick, pause, name, request id and "
            + "direction; a save while the clock runs is refused INVALID_REQUEST; a wrong expected tick is uncertain with the observed identity and tick and a "
            + "detail; lifecycle_read_save replays the completed outcome by request id and refuses an unknown id NOT_FOUND. "
            + "The save runs in RimWorld's own SaveGame path, which no unit test reaches.",
        Ticks = 600,
        Tier = CaseTier.Nightly,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var (identity, tick, paused) = await TypedLifecycle.IdentityAsync(s, "identity-before", cancellationToken).ConfigureAwait(false);
        if (!paused)
        {
            throw new AcceptanceException("identity-before: the lab did not open paused");
        }

        // A paused save with the matching expected tick completes and echoes exactly what it was asked.
        var happyId = TypedLifecycle.Stamp("checkpoint-happy");
        var happyName = TypedLifecycle.Stamp("checkpoint-happy");
        var happy = await s.CallAsync(
            "save-happy",
            NativeOps.SaveGame,
            new Lifecycle.SaveRequest { Player = TypedLifecycle.Player(identity, happyId), SaveName = happyName, ExpectedTick = tick },
            cancellationToken).ConfigureAwait(false);
        var completed = happy.Outcome?.Completed ?? throw new AcceptanceException($"save-happy: expected a completed save, got {happy.Outcome}");
        s.RecordWire("save_happy", completed);
        if (completed.Context?.Identity != identity || completed.Context.Tick != tick || completed.Paused != true
            || completed.SaveName != happyName || completed.RequestId != happyId || completed.PlayerDirection != TypedLifecycle.Direction)
        {
            throw new AcceptanceException($"save-happy: the completed save does not echo identity {identity}, tick {tick}, paused, {happyName}, {happyId}: {completed}");
        }

        // A save while the clock runs is refused: the probe saves on the first poll that reads the game running.
        Lifecycle.SaveReply? running = null;
        _ = await s.RunUntilAsync(
            "save-running",
            600,
            async ct =>
            {
                var clock = await s.CallAsync("save-running-tick", NativeOps.ReadTick, new Lifecycle.TickRequest(), ct).ConfigureAwait(false);
                if (clock.Outcome?.Loaded is not { Paused: false })
                {
                    return (Wait.Signature("paused"), false);
                }

                running = await s.CallAsync(
                    "save-running",
                    NativeOps.SaveGame,
                    new Lifecycle.SaveRequest { Player = TypedLifecycle.Player(identity, TypedLifecycle.Stamp("checkpoint-running")), SaveName = TypedLifecycle.Stamp("checkpoint-running") },
                    ct).ConfigureAwait(false);
                return (string.Empty, true);
            },
            cancellationToken).ConfigureAwait(false);
        TypedLifecycle.ExpectFailure("save-running", running?.Outcome?.Failure, Common.FailureCode.InvalidRequest, running?.Outcome);

        // Paused again, a deliberately wrong expected tick is uncertain, never completed.
        var (now, nowTick, nowPaused) = await TypedLifecycle.IdentityAsync(s, "identity-before-uncertain", cancellationToken).ConfigureAwait(false);
        if (!nowPaused)
        {
            throw new AcceptanceException("identity-before-uncertain: the game did not pause after the run");
        }

        var uncertainId = TypedLifecycle.Stamp("checkpoint-uncertain");
        var uncertainName = TypedLifecycle.Stamp("checkpoint-uncertain");
        var uncertainReply = await s.CallAsync(
            "save-uncertain",
            NativeOps.SaveGame,
            new Lifecycle.SaveRequest { Player = TypedLifecycle.Player(now, uncertainId), SaveName = uncertainName, ExpectedTick = nowTick + 1000 },
            cancellationToken).ConfigureAwait(false);
        var uncertain = uncertainReply.Outcome?.Uncertain ?? throw new AcceptanceException($"save-uncertain: expected an uncertain outcome, got {uncertainReply.Outcome}");
        s.RecordWire("save_uncertain", uncertain);
        // The refusal carries what native observed instead, so the caller can re-anchor on the real tick.
        if (uncertain.RequestId != uncertainId || uncertain.SaveName != uncertainName || string.IsNullOrEmpty(uncertain.Detail)
            || uncertain.ObservedContext?.Identity != now || uncertain.ObservedContext.Tick != nowTick)
        {
            throw new AcceptanceException($"save-uncertain: want {uncertainId} / {uncertainName} with a detail, observed at tick {nowTick} on {now}, got {uncertain}");
        }

        // lifecycle_read_save replays the completed save by request id, so a caller who lost the reply recovers it.
        var replay = await s.CallAsync("read-save-happy", NativeOps.PollSave, new Lifecycle.RequestStatus { RequestId = happyId }, cancellationToken).ConfigureAwait(false);
        var replayed = replay.Outcome?.Completed ?? throw new AcceptanceException($"read-save-happy: expected a completed save, got {replay.Outcome}");
        if (replayed.RequestId != happyId || replayed.SaveName != happyName || replayed.Context?.Identity != identity)
        {
            throw new AcceptanceException($"read-save-happy: the replay does not match the original save: {replayed}");
        }

        var unknown = await s.CallAsync("read-save-unknown", NativeOps.PollSave, new Lifecycle.RequestStatus { RequestId = TypedLifecycle.Stamp("checkpoint-unknown") }, cancellationToken).ConfigureAwait(false);
        TypedLifecycle.ExpectFailure("read-save-unknown", unknown.Outcome?.Failure, Common.FailureCode.NotFound, unknown.Outcome);
    }
}
