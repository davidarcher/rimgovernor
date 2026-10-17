using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Control = RimGovernor.Contracts.Wire.Authority;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/shutdown</c>: unloading the game with Auto granted revokes authority as SHUTDOWN at generation+1, and
/// the ended identity's status reports that retained state with its final tick at the main menu. A game loaded
/// afterwards reports the ended identity stale and its own authority unowned. Process exit (Root.Shutdown) is hooked
/// the same way but is not wire-observable.
/// </summary>
internal static class LifecycleShutdown
{
    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/shutdown",
        Scope = "lifecycle_menu with Auto granted ends the game: authority_read_status for the ended identity reports "
            + "Inactive(SHUTDOWN) at generation+1 with a tick no earlier than the unload tick; a checkpoint of the same "
            + "colony loaded afterwards with lifecycle_load reports the ended identity STALE_IDENTITY and its own authority "
            + "not active (generation 1 when inactive). The unload runs RimWorld's own scene change. NoKeep: the case ends on "
            + "a game the next case did not open.",
        Ticks = 0,
        Tier = CaseTier.Nightly,
        NoKeep = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var identity = s.Identity;
        var expected = await TypedAuthority.ExpectedGenerationAsync(s, "status-fresh", identity, cancellationToken).ConfigureAwait(false);
        var granted = await TypedAuthority.GrantAsync(s, "grant", identity, expected, cancellationToken).ConfigureAwait(false);
        if (granted != expected + 1)
        {
            throw new AcceptanceException($"grant: expected generation {expected + 1}, got {granted}");
        }

        // The game is paused, so the tick read now is the tick it ends at. The checkpoint is what the case loads afterwards.
        var (_, unloadTick, _) = await TypedLifecycle.IdentityAsync(s, "unload-tick", cancellationToken).ConfigureAwait(false);
        var saveName = TypedLifecycle.Stamp("shutdown-checkpoint");
        _ = await TypedLifecycle.SaveAsync(s, "checkpoint", identity, saveName, cancellationToken).ConfigureAwait(false);

        var menu = await MenuAsync(s, "unload", identity, cancellationToken).ConfigureAwait(false);
        if (menu.Unloaded != true)
        {
            throw new AcceptanceException($"unload: expected the loaded game to be ended, got {menu}");
        }

        // At the main menu the ended identity's retained authority is the only state left to report.
        Control.Status ended = null!;
        await TypedLifecycle.UntilAsync(
            "status-after-unload",
            async ct =>
            {
                var reply = await s.CallAsync("status-after-unload", NativeOps.ReadAuthorityStatus, new Control.StatusRequest { Identity = identity }, ct).ConfigureAwait(false);
                if (reply.Outcome?.Status is { } status)
                {
                    ended = status;
                    return (Wait.Signature(status.State), status.State?.Inactive is not null);
                }

                return reply.Outcome?.Failure?.Code == Common.FailureCode.Unavailable
                    ? (Wait.Signature("unavailable"), false)
                    : throw new AcceptanceException($"status-after-unload: expected a status or a transient unavailable failure, got {reply.Outcome}");
            },
            cancellationToken).ConfigureAwait(false);
        s.RecordWire("status_after_unload", ended);
        if (ended.State?.Inactive?.Reason != Control.RevocationReason.Shutdown || ended.Context?.Identity != identity
            || ended.Context.NativeGeneration != granted + 1 || ended.Context.Tick < unloadTick)
        {
            throw new AcceptanceException($"status-after-unload: want Inactive(SHUTDOWN) for {identity} at generation {granted + 1}, tick >= {unloadTick}; got {ended}");
        }

        // The process survived the unload, and the next game does not inherit the retained state.
        var loaded = await TypedLifecycle.LoadAsync(s, "load-after", identity, saveName, Lifecycle.Readiness.Map, cancellationToken).ConfigureAwait(false);
        var next = loaded.Loaded?.Context?.Identity ?? throw new AcceptanceException($"load-after: no loaded identity: {loaded}");
        if (next == identity)
        {
            throw new AcceptanceException($"load-after: expected a fresh identity, got the ended game's {next}");
        }

        var stale = await s.CallAsync("status-old-identity", NativeOps.ReadAuthorityStatus, new Control.StatusRequest { Identity = identity }, cancellationToken).ConfigureAwait(false);
        TypedLifecycle.ExpectFailure("status-old-identity", stale.Outcome?.Failure, Common.FailureCode.StaleIdentity, stale.Outcome);

        var fresh = await TypedAuthority.StatusAsync(s, "status-next", next, cancellationToken).ConfigureAwait(false);
        s.RecordWire("status_next", fresh);
        if (fresh.State?.Active is not null || (fresh.State?.Inactive is not null && fresh.Context?.NativeGeneration != 1))
        {
            throw new AcceptanceException($"status-next: the loaded game inherited authority: {fresh}");
        }
    }

    /// <summary>Returns to the main menu through lifecycle_menu, naming the game it ends, and polls it to its completed outcome.</summary>
    private static async Task<Lifecycle.MenuCompleted> MenuAsync(ITypedSession s, string label, Common.Identity identity, CancellationToken cancellationToken)
    {
        var requestId = TypedLifecycle.Stamp(label);
        var reply = await s.CallAsync(
            label + "-start",
            NativeOps.StartMenu,
            new Lifecycle.MenuRequest { RequestId = requestId, ExpectedPlayer = TypedLifecycle.Player(identity, requestId) },
            cancellationToken).ConfigureAwait(false);
        Lifecycle.MenuCompleted? completed = null;
        var poll = 0;
        await TypedLifecycle.UntilAsync(
            label,
            async ct =>
            {
                if (poll++ > 0)
                {
                    reply = await s.CallAsync(label + "-poll", NativeOps.PollMenu, new Lifecycle.RequestStatus { RequestId = requestId }, ct).ConfigureAwait(false);
                }

                if (reply.Outcome?.Completed is { } done)
                {
                    completed = done;
                    return (string.Empty, true);
                }

                return reply.Outcome?.Pending is { } pending
                    ? (Wait.Signature("pending", pending.Detail), false)
                    : throw new AcceptanceException($"{label}: expected a pending or completed return to the menu, got {reply.Outcome}");
            },
            cancellationToken).ConfigureAwait(false);
        return completed!;
    }
}
