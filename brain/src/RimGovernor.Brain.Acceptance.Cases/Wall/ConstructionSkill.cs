using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>wall/construction-skill</c> (Go <c>construction_skill.go</c>).</summary>
internal static class WallConstructionSkill
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/construction-skill",
        Start = new LabStart(),
        Crew = new Crew { Size = 3 },
        Budget = TimeSpan.FromMinutes(4),
        RequiredOps = ["test/construction_skill"],
        Scope = "A native finishing-skill setting transfers from blueprint to frame and survives save/load; low-skill normal/prioritized finishing and direct completion are refused while a safe material-filled wall progresses through vanilla work. Equal-skilled completion succeeds and a same-cell replacement inherits nothing. The construction tier (#2522) placed with the floor survives conversion and save/load, set-tier re-tiers the frame, a stale target id is refused and the floor stays write-once. Native because Go snapshots cannot prove Harmony work-giver hooks, save references, conversion or pawn construction.",
        Run = RunAsync,
    };

    private static readonly string[] ProgressFlags = ["lowRefused", "highAllowed", "bedPending"];

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;

        async Task<JsonObject> Call(string action)
        {
            var row = await h.CallAsync("construction-skill-" + action, "test/construction_skill", new JsonObject { ["action"] = action }, cancellationToken).ConfigureAwait(false);
            return row["success"].AsBool() == true ? row : throw new AcceptanceException($"construction skill {action}: {row.ToJson()}");
        }

        var prepared = await Call("prepare").ConfigureAwait(false);
        var id = await Session.ReadIdentityAsync(h, "construction-skill-identity", cancellationToken).ConfigureAwait(false);
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "construction-skill-authority", id, cancellationToken).ConfigureAwait(false);

        Task<JsonObject> Building(string label, JsonObject intent)
        {
            intent["placement"] = new JsonObject
            {
                ["defName"] = "Bed",
                ["stuff"] = "WoodLog",
                ["x"] = prepared["x"]?.DeepClone(),
                ["z"] = prepared["z"]?.DeepClone(),
                ["rotation"] = "ROTATION_NORTH",
            };
            return ApplyIntents.ApplyOneAsync(h, label, id, label, new JsonObject { ["building"] = intent }, cancellationToken);
        }

        _ = await Building("construction-minimum", new JsonObject { ["minimumFinishingSkill"] = 9, ["tier"] = 3, ["existingTargetId"] = prepared["target"]?.DeepClone() }).ConfigureAwait(false);
        var converted = await Call("convert").ConfigureAwait(false);
        if (converted["minimum"].AsNumber() != 9 || converted["tier"].AsNumber() != 3)
        {
            throw new AcceptanceException($"conversion lost minimum or tier: {converted.ToJson()}");
        }

        // The blueprint id is stale once the frame stands; a different tier on the frame is accepted, and the write-once finishing floor refuses a different minimum.
        var stale = await Building("construction-tier-stale", new JsonObject { ["tier"] = 1, ["existingTargetId"] = prepared["target"]?.DeepClone() }).ConfigureAwait(false);
        ApplyIntents.Refused("construction-tier-stale", stale, "stale");
        var retiered = await Building("construction-tier-set", new JsonObject { ["tier"] = 1, ["existingTargetId"] = converted["target"]?.DeepClone() }).ConfigureAwait(false);
        if (retiered["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"set-tier not applied: {retiered.ToJson()}");
        }

        var floor = await Building("construction-floor-change", new JsonObject { ["minimumFinishingSkill"] = 10, ["existingTargetId"] = converted["target"]?.DeepClone() }).ConfigureAwait(false);
        ApplyIntents.Refused("construction-floor-change", floor, "already set");
        var audit = await Call("audit").ConfigureAwait(false);
        if (audit["tier"].AsNumber() != 1 || audit["minimum"].AsNumber() != 9)
        {
            throw new AcceptanceException($"set-tier or floor readback: {audit.ToJson()}");
        }

        var name = "construction-skill-" + CaseSupport.Tag((DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100);
        var saved = await h.WireAsync(
            "skill-save",
            "lifecycle_save",
            new JsonObject
            {
                ["player"] = new JsonObject { ["identity"] = id.DeepClone(), ["playerDirection"] = 1, ["requestId"] = name },
                ["saveName"] = name,
            },
            cancellationToken).ConfigureAwait(false);
        _ = Harness.Outcome(saved, "completed");
        var loadId = name + "-load";
        var loaded = await h.WireAsync(
            "skill-load",
            "lifecycle_load",
            new JsonObject
            {
                ["requestId"] = loadId,
                ["saveName"] = name,
                ["readiness"] = "READINESS_MAP",
                ["expectedPlayer"] = new JsonObject { ["identity"] = id.DeepClone(), ["playerDirection"] = 1, ["requestId"] = loadId },
                ["playerDirection"] = 1,
            },
            cancellationToken).ConfigureAwait(false);
        _ = await GameReuse.PollLoadAsync(h, loaded, loadId, "skill-load-poll", cancellationToken).ConfigureAwait(false);

        // RunUntil uses this harness's fresh identity, not the pre-load Session snapshot.
        _ = await RunAt.RunUntilAsync(
            h,
            "wall-progress",
            6000,
            new Wait(),
            async _ =>
            {
                var row = await Call("audit").ConfigureAwait(false);
                if (row["minimum"].AsNumber() != 9 || row["tier"].AsNumber() != 1)
                {
                    throw new AcceptanceException($"save lost minimum or tier: {row.ToJson()}");
                }

                foreach (var key in ProgressFlags)
                {
                    if (row[key].AsBool() != true)
                    {
                        throw new AcceptanceException($"{key}: {row.ToJson()}");
                    }
                }

                return ("material-filled wall", row["wallProgress"].AsBool() == true);
            },
            cancellationToken).ConfigureAwait(false);
        var controls = await Call("controls").ConfigureAwait(false);
        foreach (var key in new[] { "lowRefused", "lowCompletionRefused", "completed", "noLeak" })
        {
            if (controls[key].AsBool() != true)
            {
                throw new AcceptanceException($"{key}: {controls.ToJson()}");
            }
        }

        s.Report["construction_skill"] = controls;
    }
}
