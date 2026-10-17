using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/load</c>: the async native load and the governor state that rides the save. A fresh game carries no
/// governor state; the batched put replaces the whole set; a checkpoint save loaded at MAP readiness comes back as the
/// same colony under a fresh load token with the blob intact; a VISUAL-readiness load completes; an unknown request id
/// is refused. The save handshake's idle wait times out and a token native never issued is refused.
/// </summary>
internal static class LifecycleLoad
{
    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/load",
        Scope = "lifecycle_load on the lab: governor state is empty on a fresh game, the batched put replaces the whole "
            + "blob set, and the blob survives a checkpoint save loaded back at MAP readiness as the same colony with a fresh "
            + "load token; a VISUAL-readiness load of the same save completes VISUAL; lifecycle_read_load refuses an unknown "
            + "request id NOT_FOUND; lifecycle_wait_save_signal times out with no save and lifecycle_flush_done refuses an "
            + "unissued token. Loading runs RimWorld's own scene change, which no unit test reaches.",
        Ticks = 0,
        Tier = CaseTier.Nightly,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var (identity, _, paused) = await TypedLifecycle.IdentityAsync(s, "identity-before", cancellationToken).ConfigureAwait(false);
        if (!paused || identity.ColonyId.Length == 0 || identity.LoadToken.Length == 0)
        {
            throw new AcceptanceException($"identity-before: want a paused lab with a colony id and load token, got {identity} (paused {paused})");
        }

        var fresh = await GovernorStateAsync(s, "governor-state-fresh", cancellationToken).ConfigureAwait(false);
        if (fresh.Count != 0)
        {
            throw new AcceptanceException($"governor-state-fresh: want no blobs, got {fresh.Count}");
        }

        // Save handshake: with no save running the held wait returns its timeout, and an unissued token is refused.
        var wait = await s.CallAsync("save-signal-wait", NativeOps.WaitSaveSignal, new Lifecycle.WaitSaveSignalRequest { TimeoutMs = 200 }, cancellationToken).ConfigureAwait(false);
        if (wait.Outcome?.Timeout is null)
        {
            throw new AcceptanceException($"save-signal-wait: want a timeout, got {wait.Outcome}");
        }

        var flush = await s.CallAsync("save-signal-stale", NativeOps.FlushDone, new Lifecycle.FlushDoneRequest { Token = "never-issued" }, cancellationToken).ConfigureAwait(false);
        if (flush.Outcome?.Failure is null)
        {
            throw new AcceptanceException($"save-signal-stale: want a refusal, got {flush.Outcome}");
        }

        // The batched put replaces the whole set: a second key goes in with the probe, then a batch without it removes it.
        var blob = "{\"probe\":[" + string.Concat(Enumerable.Repeat("{\"id\":\"p\",\"payload\":{}},", 200)) + "{}]}";
        await PutAsync(s, "governor-state-batch-add", new Dictionary<string, string>(StringComparer.Ordinal) { ["probe"] = blob, ["stale"] = "x" }, cancellationToken).ConfigureAwait(false);
        var added = await GovernorStateAsync(s, "governor-state-batch-add-readback", cancellationToken).ConfigureAwait(false);
        if (added.Count != 2 || added.GetValueOrDefault("probe") != blob || added.GetValueOrDefault("stale") != "x")
        {
            throw new AcceptanceException($"governor-state-batch-add-readback: want probe and stale, got {string.Join(", ", added.Keys)}");
        }

        await PutAsync(s, "governor-state-batch-replace", new Dictionary<string, string>(StringComparer.Ordinal) { ["probe"] = blob }, cancellationToken).ConfigureAwait(false);
        var replaced = await GovernorStateAsync(s, "governor-state-readback", cancellationToken).ConfigureAwait(false);
        if (replaced.Count != 1 || replaced.GetValueOrDefault("probe") != blob)
        {
            throw new AcceptanceException($"governor-state-readback: want only probe, got {string.Join(", ", replaced.Keys)}");
        }

        var saveName = TypedLifecycle.Stamp("load-checkpoint");
        var saved = await TypedLifecycle.SaveAsync(s, "setup-save", identity, saveName, cancellationToken).ConfigureAwait(false);
        s.RecordWire("setup_save", saved);

        // A MAP-readiness load of that save: the same colony under a fresh load token, the blob intact.
        var map = await TypedLifecycle.LoadAsync(s, "load-map", identity, saveName, Lifecycle.Readiness.Map, cancellationToken).ConfigureAwait(false);
        s.RecordWire("load_map", map);
        var loaded = map.Loaded?.Context?.Identity ?? throw new AcceptanceException($"load-map: no loaded identity: {map}");
        if (loaded.ColonyId != identity.ColonyId || loaded.LoadToken.Length == 0 || loaded.LoadToken == identity.LoadToken
            || map.SaveName != saveName || map.Readiness != Lifecycle.Readiness.Map)
        {
            throw new AcceptanceException($"load-map: want colony {identity.ColonyId} under a fresh load token at MAP readiness from {saveName}, got {map}");
        }

        var survived = await GovernorStateAsync(s, "governor-state-loaded", cancellationToken).ConfigureAwait(false);
        if (survived.Count != 1 || survived.GetValueOrDefault("probe") != blob)
        {
            throw new AcceptanceException("governor-state-loaded: the blob did not survive the save and load");
        }

        // A VISUAL-readiness load completes only once the map has been drawn.
        var visual = await TypedLifecycle.LoadAsync(s, "load-visual", loaded, saveName, Lifecycle.Readiness.Visual, cancellationToken).ConfigureAwait(false);
        s.RecordWire("load_visual", visual);
        if (visual.Readiness != Lifecycle.Readiness.Visual)
        {
            throw new AcceptanceException($"load-visual: want VISUAL readiness, got {visual}");
        }

        var unknown = await s.CallAsync("read-load-unknown", NativeOps.PollLoad, new Lifecycle.RequestStatus { RequestId = TypedLifecycle.Stamp("load-unknown") }, cancellationToken).ConfigureAwait(false);
        TypedLifecycle.ExpectFailure("read-load-unknown", unknown.Outcome?.Failure, Common.FailureCode.NotFound, unknown.Outcome);
    }

    private static async Task PutAsync(ITypedSession s, string label, Dictionary<string, string> blobs, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.PutGovernorStateBatch, new Lifecycle.PutGovernorStateBatchRequest { Blobs = blobs }, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome?.Loaded is null)
        {
            throw new AcceptanceException($"{label}: the put was not accepted: {reply.Outcome}");
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> GovernorStateAsync(ITypedSession s, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadGovernorState, new Lifecycle.GovernorStateRequest(), cancellationToken).ConfigureAwait(false);
        return reply.Outcome?.Loaded?.Blobs ?? throw new AcceptanceException($"{label}: no governor state: {reply.Outcome}");
    }
}
