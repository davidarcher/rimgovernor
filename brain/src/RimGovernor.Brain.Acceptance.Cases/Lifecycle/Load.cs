using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/load</c> (Go <c>load.go</c>): the native-acceptance run for the async native load: governor state, a
/// setup checkpoint save, a MAP-readiness load polled to completion, a VISUAL-readiness load and an unknown-request
/// rejection. It never exercises reconnect-after-disconnect or competing-viewer arbitration.
/// </summary>
internal static class LifecycleLoad
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/load",
        Scope = "Trusted native rimgovernor/lifecycle_load: governor state (#882) empty on a fresh game, a put blob surviving the save and load with its save-size delta reported; a setup checkpoint save, an async MAP-readiness load of that save polled via rimgovernor/lifecycle_read_load to LoadCompleted with map_ready and a fresh load token, a second VISUAL-readiness load of the same save reaching a completed VISUAL outcome, and an unknown-request-id rejection. No reconnect/competing-viewer capability exercised.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        LifecycleCases.RequireTools(s, "rimgovernor/lifecycle_load", "rimgovernor/lifecycle_read_load");
        var (loaded, identity, _) = await LifecycleCases.ReadIdentityAsync(h, "identity-before", cancellationToken).ConfigureAwait(false);
        if (loaded["paused"].AsBool() != true)
        {
            throw new AcceptanceException("fresh debug game did not start paused");
        }

        var originalColonyId = identity["colonyId"].AsString();
        var originalLoadToken = identity["loadToken"].AsString();
        if (originalColonyId.Length == 0 || originalLoadToken.Length == 0)
        {
            throw new AcceptanceException("missing colony id or load token before setup save");
        }

        // Governor state: a fresh game carries none, and a put blob rides the setup save. The save-size delta of that
        // blob is reported.
        var fresh = await GovernorStateAsync(h, "governor-state-fresh", "lifecycle_read_governor_state", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (fresh.Count != 0)
        {
            throw new AcceptanceException($"governor-state-fresh: want no blobs, got {fresh.Count}");
        }

        var emptySize = await SaveAsync(s, "size-save", identity, $"loadaccept-size-{LifecycleCases.Stamp()}", cancellationToken).ConfigureAwait(false);

        // Save handshake: the client serializes calls, so a held wait cannot overlap a save here. With no signal the
        // wait returns its timeout, and a token native never issued is rejected rather than acked.
        var waitReply = await h.WireAsync("save-signal-wait", "lifecycle_wait_save_signal", new JsonObject { ["timeoutMs"] = 200 }, cancellationToken).ConfigureAwait(false);
        _ = LifecycleCases.Outcome("save-signal-wait: want timeout", waitReply, "timeout");
        var flushReply = await h.WireAsync("save-signal-stale", "lifecycle_flush_done", new JsonObject { ["token"] = "never-issued" }, cancellationToken).ConfigureAwait(false);
        _ = LifecycleCases.Outcome("save-signal-stale: want failure", flushReply, "failure");
        var governorBlob = "{\"probe\":[" + string.Concat(Enumerable.Repeat("{\"id\":\"p\",\"payload\":{}},", 200)) + "{}]}";

        // The batched off-thread put replaces the whole set: a second key goes in with the probe, then a batch without
        // it removes it by absence.
        _ = await GovernorStateAsync(
            h,
            "governor-state-batch-add",
            "lifecycle_put_governor_state_batch",
            new JsonObject { ["blobs"] = new JsonObject { ["probe"] = governorBlob, ["stale"] = "x" } },
            cancellationToken).ConfigureAwait(false);
        var added = await GovernorStateAsync(h, "governor-state-batch-add-readback", "lifecycle_read_governor_state", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (!added.TryGetValue("probe", out var probe) || probe != governorBlob || !added.TryGetValue("stale", out var stale) || stale != "x" || added.Count != 2)
        {
            throw new AcceptanceException($"governor-state-batch-add-readback: {added.Count} blobs");
        }

        _ = await GovernorStateAsync(
            h,
            "governor-state-batch-replace",
            "lifecycle_put_governor_state_batch",
            new JsonObject { ["blobs"] = new JsonObject { ["probe"] = governorBlob } },
            cancellationToken).ConfigureAwait(false);
        var replaced = await GovernorStateAsync(h, "governor-state-readback", "lifecycle_read_governor_state", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (!replaced.TryGetValue("probe", out var stored) || stored != governorBlob)
        {
            throw new AcceptanceException("governor-state-readback: blob not stored");
        }

        // Setup: a trusted checkpoint save this run then loads back.
        var setupSaveName = $"loadaccept-checkpoint-{LifecycleCases.Stamp()}";
        var setupSize = await SaveAsync(s, "setup-save", identity, setupSaveName, cancellationToken).ConfigureAwait(false);
        report["setup_save"] = setupSaveName;
        report["governor_state_save_bytes"] = new JsonObject { ["blob"] = governorBlob.Length, ["saveDelta"] = setupSize - emptySize };

        // Case 1: happy path load. Start the load; native may complete synchronously or return LoadPending, in which
        // case ReadLoad is polled until a definite outcome.
        var loadRequestId = $"loadaccept-happy-{LifecycleCases.Stamp()}";
        var loadReply = await h.WireAsync("load-start", "lifecycle_load", LoadRequest(loadRequestId, setupSaveName, "READINESS_MAP", identity), cancellationToken).ConfigureAwait(false);
        JsonObject completed;
        try
        {
            completed = await GameReuse.PollLoadAsync(h, loadReply, loadRequestId, "load-poll", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"load-happy: {e.Message}", e);
        }

        var afterIdentity = completed["loaded"].AsMap()?["context"].AsMap()?["identity"].AsMap() ?? [];
        if (afterIdentity["colonyId"].AsString() != originalColonyId)
        {
            throw new AcceptanceException("load-happy: loaded colony id does not match the saved colony");
        }

        var newToken = afterIdentity["loadToken"].AsString();
        if (newToken == originalLoadToken || newToken.Length == 0)
        {
            throw new AcceptanceException("load-happy: loaded map did not receive a fresh load token");
        }

        if (completed["saveName"].AsString() != setupSaveName)
        {
            throw new AcceptanceException("load-happy: completed save name mismatch");
        }

        if (completed["requestId"].AsString() != loadRequestId)
        {
            throw new AcceptanceException("load-happy: completed request id mismatch");
        }

        if (completed["readiness"].AsString() != "READINESS_MAP")
        {
            throw new AcceptanceException($"load-happy: expected READINESS_MAP, got \"{completed["readiness"].AsString()}\"");
        }

        report["case_happy"] = new JsonObject { ["saveName"] = setupSaveName, ["colonyId"] = originalColonyId, ["newLoadToken"] = newToken };
        var after = await GovernorStateAsync(h, "governor-state-loaded", "lifecycle_read_governor_state", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (!after.TryGetValue("probe", out var survived) || survived != governorBlob || after.Count != 1)
        {
            throw new AcceptanceException("governor-state-loaded: blob did not survive the save and load");
        }

        report["case_governor_state_round_trip"] = true;

        // Case 1b: a VISUAL-readiness load of the same save must not complete until the map has actually been drawn, not
        // merely MAP-ready with data in memory. The debug game was started with readiness "visual" (so it is already
        // rendering), so this load should still reach a completed VISUAL outcome within the polling budget.
        var visualRequestId = $"loadaccept-visual-{LifecycleCases.Stamp()}";
        var visualLoadReply = await h.WireAsync("load-visual-start", "lifecycle_load", LoadRequest(visualRequestId, setupSaveName, "READINESS_VISUAL", afterIdentity), cancellationToken).ConfigureAwait(false);
        JsonObject visualCompleted;
        try
        {
            visualCompleted = await GameReuse.PollLoadAsync(h, visualLoadReply, visualRequestId, "load-visual-poll", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"load-visual: {e.Message}", e);
        }

        if (visualCompleted["readiness"].AsString() != "READINESS_VISUAL")
        {
            throw new AcceptanceException($"load-visual: expected READINESS_VISUAL, got \"{visualCompleted["readiness"].AsString()}\"");
        }

        report["case_visual_readiness"] = true;

        // Case 2: ReadLoad for an unknown/expired request id must be refused, not silently treated as pending forever
        // or completed.
        var unknownReply = await h.WireAsync("read-load-unknown", "lifecycle_read_load", new JsonObject { ["requestId"] = $"loadaccept-unknown-{LifecycleCases.Stamp()}" }, cancellationToken).ConfigureAwait(false);
        LifecycleCases.ExpectFailure("read-load-unknown", unknownReply, "FAILURE_CODE_NOT_FOUND");
        report["case_unknown_request_id"] = true;
    }

    private static JsonObject LoadRequest(string requestId, string saveName, string readiness, JsonObject identity) => new()
    {
        ["requestId"] = requestId,
        ["saveName"] = saveName,
        ["readiness"] = readiness,
        ["expectedPlayer"] = new JsonObject { ["identity"] = identity.DeepClone(), ["playerDirection"] = 1, ["requestId"] = requestId },
        ["playerDirection"] = 1,
    };

    /// <summary>Takes a trusted checkpoint save and returns its file size.</summary>
    private static async Task<long> SaveAsync(ISession s, string label, JsonObject identity, string name, CancellationToken cancellationToken)
    {
        var reply = await s.Harness.WireAsync(
            label,
            "lifecycle_save",
            new JsonObject
            {
                ["player"] = new JsonObject { ["identity"] = identity.DeepClone(), ["playerDirection"] = 1, ["requestId"] = $"loadaccept-{label}-{LifecycleCases.Stamp()}" },
                ["saveName"] = name,
            },
            cancellationToken).ConfigureAwait(false);
        _ = LifecycleCases.Outcome($"{label}: expected a completed save", reply, "completed");
        return new FileInfo(s.Config.ProfileSave(name)).Length;
    }

    /// <summary>Calls a governor-state method and returns its loaded blobs.</summary>
    private static async Task<Dictionary<string, string>> GovernorStateAsync(Harness h, string label, string method, JsonObject request, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, method, request, cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(reply, "loaded");
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in loaded["blobs"].AsMap() ?? [])
        {
            blobs[key] = value.AsString();
        }

        return blobs;
    }
}
