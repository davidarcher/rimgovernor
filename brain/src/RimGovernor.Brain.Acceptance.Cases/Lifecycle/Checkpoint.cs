using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/checkpoint</c> (Go <c>checkpoint.go</c>): the native-acceptance run for the trusted native
/// save-checkpoint slice. It never exercises reconnect-after-disconnect or competing-viewer arbitration.
/// </summary>
internal static class LifecycleCheckpoint
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/checkpoint",
        Scope = "Trusted native rimgovernor/lifecycle_save checkpoint: paused happy-path completed save with identity/tick/direction/pause verification, unpaused refusal, and wrong-expected-tick uncertain outcome; plus rimgovernor/lifecycle_read_save replaying the happy-path outcome by request_id and refusing an unknown request_id. No reconnect/competing-viewer capability exercised.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        LifecycleCases.RequireTools(s, "rimgovernor/lifecycle_save");
        var (loaded, identity, tick) = await LifecycleCases.ReadIdentityAsync(h, "identity-before", cancellationToken).ConfigureAwait(false);
        if (loaded["paused"].AsBool() != true)
        {
            throw new AcceptanceException("fresh debug game did not start paused");
        }

        // Case 1: paused happy path. A fresh save name with a matching expected tick must complete, echoing exactly the
        // identity/tick/direction/pause/name/request id the request asked for.
        var happyRequestId = $"checkpointaccept-happy-{LifecycleCases.Stamp()}";
        var happySaveName = $"checkpointaccept-happy-{LifecycleCases.Stamp()}";
        var happyReply = await h.WireAsync(
            "save-happy",
            "lifecycle_save",
            new JsonObject
            {
                ["player"] = new JsonObject { ["identity"] = identity.DeepClone(), ["playerDirection"] = 1, ["requestId"] = happyRequestId },
                ["saveName"] = happySaveName,
                ["expectedTick"] = tick,
            },
            cancellationToken).ConfigureAwait(false);
        var (_, completed) = LifecycleCases.Outcome("save-happy: expected a completed save", happyReply, "completed");
        var completedContext = completed["context"].AsMap();
        if (!Dyn.DeepEqual(completedContext?["identity"], identity))
        {
            throw new AcceptanceException("save-happy: completed identity does not match the request");
        }

        if (completedContext.Member("tick").AsNumber() != tick)
        {
            throw new AcceptanceException($"save-happy: completed tick {completedContext.Member("tick").ToJson()} does not match expected {tick}");
        }

        if (completed["paused"].AsBool() != true)
        {
            throw new AcceptanceException("save-happy: completed save did not report paused");
        }

        if (completed["saveName"].AsString() != happySaveName)
        {
            throw new AcceptanceException("save-happy: completed save name mismatch");
        }

        if (completed["requestId"].AsString() != happyRequestId)
        {
            throw new AcceptanceException("save-happy: completed request id mismatch");
        }

        if (completed["playerDirection"].AsNumber() != 1)
        {
            throw new AcceptanceException("save-happy: completed player direction mismatch");
        }

        report["case_happy"] = new JsonObject { ["saveName"] = happySaveName, ["tick"] = tick };

        // Case 2: attempt a save while NOT paused. Identity is reused unchanged (colony/load/map do not move while
        // ticking) and expected_tick is intentionally omitted, so the refusal exercises the actual reason under test
        // (native not paused) rather than a stale-tick race.
        _ = await h.CallAsync("unpause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Normal", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var unpausedReply = await h.WireAsync(
            "save-unpaused",
            "lifecycle_save",
            new JsonObject
            {
                ["player"] = new JsonObject { ["identity"] = identity.DeepClone(), ["playerDirection"] = 1, ["requestId"] = $"checkpointaccept-unpaused-{LifecycleCases.Stamp()}" },
                ["saveName"] = $"checkpointaccept-unpaused-{LifecycleCases.Stamp()}",
            },
            cancellationToken).ConfigureAwait(false);
        LifecycleCases.ExpectFailure("save-unpaused", unpausedReply, "FAILURE_CODE_INVALID_REQUEST");
        report["case_unpaused"] = true;

        // Case 3: re-pause, then attempt a save with a deliberately wrong expected_tick. Identity is re-read fresh (tick
        // advanced while unpaused above) so only the deliberate tick mismatch triggers the refusal.
        _ = await h.CallAsync("repause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var (loadedForUncertain, uncertainIdentity, uncertainTick) = await LifecycleCases.ReadIdentityAsync(h, "identity-before-uncertain", cancellationToken).ConfigureAwait(false);
        if (loadedForUncertain["paused"].AsBool() != true)
        {
            throw new AcceptanceException("game did not report paused before the uncertain-tick case");
        }

        var uncertainRequestId = $"checkpointaccept-uncertain-{LifecycleCases.Stamp()}";
        var uncertainSaveName = $"checkpointaccept-uncertain-{LifecycleCases.Stamp()}";
        var uncertainReply = await h.WireAsync(
            "save-uncertain",
            "lifecycle_save",
            new JsonObject
            {
                ["player"] = new JsonObject { ["identity"] = uncertainIdentity.DeepClone(), ["playerDirection"] = 1, ["requestId"] = uncertainRequestId },
                ["saveName"] = uncertainSaveName,
                ["expectedTick"] = uncertainTick + 1000,
            },
            cancellationToken).ConfigureAwait(false);
        var (_, uncertain) = LifecycleCases.Outcome("save-uncertain: expected an uncertain outcome, not a completed save", uncertainReply, "uncertain");
        if (uncertain["requestId"].AsString() != uncertainRequestId)
        {
            throw new AcceptanceException("save-uncertain: uncertain request id mismatch");
        }

        if (uncertain["saveName"].AsString() != uncertainSaveName)
        {
            throw new AcceptanceException("save-uncertain: uncertain save name mismatch");
        }

        if (uncertain["detail"].AsString().Length == 0)
        {
            throw new AcceptanceException("save-uncertain: missing detail");
        }

        report["case_uncertain"] = true;

        // Case 4: rimgovernor/lifecycle_read_save replays the happy-path save's exact recorded outcome by request_id,
        // proving a caller who lost the original reply can recover it without retrying the save itself.
        LifecycleCases.RequireTools(s, "rimgovernor/lifecycle_read_save");
        var readSaveReply = await h.WireAsync("read-save-happy", "lifecycle_read_save", new JsonObject { ["requestId"] = happyRequestId }, cancellationToken).ConfigureAwait(false);
        var (_, readCompleted) = LifecycleCases.Outcome("read-save-happy: expected a completed save", readSaveReply, "completed");
        if (readCompleted["requestId"].AsString() != happyRequestId)
        {
            throw new AcceptanceException("read-save-happy: request id mismatch");
        }

        if (readCompleted["saveName"].AsString() != happySaveName)
        {
            throw new AcceptanceException("read-save-happy: save name mismatch");
        }

        if (!Dyn.DeepEqual(readCompleted["context"].AsMap()?["identity"], identity))
        {
            throw new AcceptanceException("read-save-happy: replayed identity does not match the original save");
        }

        report["case_read_save_happy"] = true;

        // Case 5: ReadSave for an unknown request id is refused, never treated as pending or completed.
        var unknownReply = await h.WireAsync("read-save-unknown", "lifecycle_read_save", new JsonObject { ["requestId"] = $"checkpointaccept-unknown-{LifecycleCases.Stamp()}" }, cancellationToken).ConfigureAwait(false);
        LifecycleCases.ExpectFailure("read-save-unknown", unknownReply, "FAILURE_CODE_NOT_FOUND");
        report["case_read_save_unknown"] = true;
    }
}
