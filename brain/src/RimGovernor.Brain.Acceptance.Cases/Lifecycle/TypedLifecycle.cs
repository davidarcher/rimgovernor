using System.Globalization;
using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Typed lifecycle steps the <c>lifecycle/*</c> cases share: the loaded identity, a trusted checkpoint save and an
/// async load polled to its definite outcome. A load or a return to the menu is a long event the game runs outside its
/// tick loop, so its wait ends on the request's completion signature under the stall budget, not on a tick.
/// </summary>
internal static class TypedLifecycle
{
    /// <summary>The direction every case request carries.</summary>
    internal const ulong Direction = 1;

    /// <summary>A unique suffix for request ids and save names.</summary>
    internal static string Stamp(string prefix) => string.Create(CultureInfo.InvariantCulture, $"{prefix}-{DateTime.UtcNow.Ticks}");

    /// <summary>The player context naming <paramref name="identity"/> for a lifecycle request.</summary>
    internal static Lifecycle.PlayerLifecycleContext Player(Common.Identity identity, string requestId) =>
        new() { Identity = identity, PlayerDirection = Direction, RequestId = requestId };

    /// <summary>The loaded game's identity, tick and pause state from <c>lifecycle_read_identity</c>.</summary>
    internal static async Task<(Common.Identity Identity, long Tick, bool Paused)> IdentityAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadIdentity, new Lifecycle.IdentityRequest(), cancellationToken).ConfigureAwait(false);
        var context = reply.Loaded?.Context ?? throw new AcceptanceException($"{label}: no loaded game");
        return (context.Identity, context.Tick, reply.Loaded.Paused == true);
    }

    /// <summary>A trusted checkpoint save of the paused game; anything but a completed save fails <paramref name="label"/>.</summary>
    internal static async Task<Lifecycle.SaveCompleted> SaveAsync(TypedSession s, string label, Common.Identity identity, string saveName, CancellationToken cancellationToken)
    {
        var request = new Lifecycle.SaveRequest { Player = Player(identity, Stamp(label)), SaveName = saveName };
        var reply = await s.CallAsync(label, NativeOps.SaveGame, request, cancellationToken).ConfigureAwait(false);
        return reply.Outcome?.Completed ?? throw new AcceptanceException($"{label}: expected a completed save, got {reply.Outcome}");
    }

    /// <summary>Starts a load of <paramref name="saveName"/> replacing <paramref name="identity"/>'s map and polls it to a completed outcome.</summary>
    internal static async Task<Lifecycle.LoadCompleted> LoadAsync(TypedSession s, string label, Common.Identity identity, string saveName, Lifecycle.Readiness readiness, CancellationToken cancellationToken)
    {
        var requestId = Stamp(label);
        var request = new Lifecycle.LoadRequest
        {
            RequestId = requestId,
            SaveName = saveName,
            Readiness = readiness,
            ExpectedPlayer = Player(identity, requestId),
        };
        var reply = await s.CallAsync(label + "-start", NativeOps.StartLoad, request, cancellationToken).ConfigureAwait(false);
        Lifecycle.LoadCompleted? completed = null;
        var poll = 0;
        await UntilAsync(
            label,
            async ct =>
            {
                if (poll++ > 0)
                {
                    reply = await s.CallAsync(label + "-poll", NativeOps.PollLoad, new Lifecycle.RequestStatus { RequestId = requestId }, ct).ConfigureAwait(false);
                }

                if (reply.Outcome?.Completed is { } done)
                {
                    completed = done;
                    return (string.Empty, true);
                }

                return reply.Outcome?.Pending is { } pending
                    ? (Wait.Signature("pending", pending.Detail), false)
                    : throw new AcceptanceException($"{label}: expected a pending or completed load, got {reply.Outcome}");
            },
            cancellationToken).ConfigureAwait(false);
        return completed!;
    }

    /// <summary>
    /// Waits for a lifecycle long event (a load, a return to the menu) to reach its definite outcome: the probe reads the
    /// request's status and the wait ends on its completion, or fails once its signature stops moving for the stall budget.
    /// </summary>
    internal static async Task UntilAsync(string label, Probe probe, CancellationToken cancellationToken)
    {
        try
        {
            await new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromMilliseconds(250) }.WaitProgressAsync(probe, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"{label}: {e.Message}", e);
        }
    }

    /// <summary>Asserts <paramref name="failure"/> is a refusal with code <paramref name="want"/>.</summary>
    internal static void ExpectFailure(string label, Common.Failure? failure, Common.FailureCode want, object? reply)
    {
        if (failure?.Code != want)
        {
            throw new AcceptanceException($"{label}: expected {want}, got {reply}");
        }
    }
}
