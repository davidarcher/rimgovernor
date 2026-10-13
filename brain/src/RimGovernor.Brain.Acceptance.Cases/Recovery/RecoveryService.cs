using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>recovery/service</c> (Go <c>recovery/service.go</c>): the disaster-recovery service vertical through
/// GiveJobIntent Repair on Actions/Apply. A genuinely damaged player Wall is repaired by a real native
/// WorkGiver_Repair job ordered for an undrafted colonist, observed via real game ticks and independently confirmed via
/// <c>observations_list_buildings</c> (HitPoints == MaxHitPoints), not just the applied result. It uses the disposable
/// <c>test/recovery_service_prepare</c> fixture: one damaged player Wall and one capable, reachable colonist.
/// </summary>
internal static class RecoveryService
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "recovery/service",
        Scope = "GiveJobIntent Repair (disaster-recovery service) dispatch: a genuinely damaged player Wall is "
            + "actually repaired by a real native WorkGiver_Repair job, unknown-pawn refusal, real HitPoints change "
            + "observed via native ticks (not just an applied result), and key replay idempotency.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/recovery_service_prepare" },
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        // Fixture: one genuinely damaged player Wall and one capable Construction-enabled colonist. Run this BEFORE
        // acquiring authority: the fixture spawns/damages a building directly outside any authority scope, and
        // NativeControlAuthority.RevokeExternal revokes any held lease for such external activity.
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        if (!Dyn.Contains(s.Names, "rimgovernor/operations_apply"))
        {
            throw new AcceptanceException("missing rimgovernor/operations_apply in discovery");
        }

        var pawnId = prepared["pawn"].AsString();
        var wallId = prepared["wall"].AsString();
        if (pawnId.Length == 0 || wallId.Length == 0)
        {
            throw new AcceptanceException($"prepare: missing fixture pawn/wall ids: {prepared.ToJson()}");
        }

        var maxHitPoints = prepared["maxHitPoints"].AsNumber();
        var damagedHitPoints = prepared["damagedHitPoints"].AsNumber();
        if (maxHitPoints <= 0 || damagedHitPoints <= 0 || damagedHitPoints >= maxHitPoints)
        {
            throw new AcceptanceException($"prepare: fixture wall is not genuinely damaged: {prepared.ToJson()}");
        }

        report["fixture_pawn"] = pawnId;
        report["fixture_wall"] = wallId;
        report["fixture_max_hit_points"] = maxHitPoints;
        report["fixture_damaged_hit_points"] = damagedHitPoints;

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        // wallState reads HitPoints/maxHitPoints through observations_list_buildings with statuses=["built"],
        // category="artificial", playerOnly=true, purely to independently assert the wall's real HitPoints
        // before/after dispatch.
        async Task<(double HitPoints, double MaxHitPoints)> WallState(string label)
        {
            var reply = await h.WireAsync(
                label,
                "observations_list_buildings",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                    ["ids"] = new JsonArray(wallId),
                    ["statuses"] = new JsonArray("built"),
                    ["category"] = "artificial",
                    ["playerOnly"] = true,
                },
                cancellationToken).ConfigureAwait(false);
            var (_, observed) = Harness.Outcome(reply, "observed");
            var rows = observed["buildings"].AsSlice();
            if (rows.Count != 1)
            {
                throw new AcceptanceException($"{label}: expected exactly one building row, got {observed.ToJson()}");
            }

            var row = rows[0].AsMap() ?? [];
            if (row["building"].AsMap()?["id"].AsString() != wallId)
            {
                throw new AcceptanceException($"{label}: unexpected building row: {row.ToJson()}");
            }

            if (!row.ContainsKey("hitPoints"))
            {
                throw new AcceptanceException($"{label}: missing hitPoints: {row.ToJson()}");
            }

            return (row["hitPoints"].AsNumber(), row["maxHitPoints"].AsNumber());
        }

        async Task<JsonObject> Apply(string key, string pawn)
        {
            var reply = await h.WireAsync(
                key,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = key, ["giveJob"] = ApplyIntents.GiveJob(pawn, "Repair", wallId)["giveJob"]?.DeepClone() }),
                },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            if (results.Count != 1)
            {
                throw new AcceptanceException($"{key}: expected one result: {reply.ToJson()}");
            }

            return results[0].AsMap() ?? [];
        }

        var (beforeHitPoints, beforeMax) = await WallState("wall-before").ConfigureAwait(false);
        if (beforeHitPoints >= beforeMax)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"wall-before: expected the fixture wall to be genuinely damaged, got hitPoints={beforeHitPoints} maxHitPoints={beforeMax}"));
        }

        report["wall_before_hit_points"] = beforeHitPoints;
        report["wall_max_hit_points"] = beforeMax;
        var unknown = await Apply("recovery-unknown-pawn", "Human_unknown_recovery_pawn").ConfigureAwait(false);
        if (unknown["refused"] is null)
        {
            throw new AcceptanceException($"recovery-unknown-pawn: expected a refusal, got {unknown.ToJson()}");
        }

        var repair = await Apply("recovery-repair", pawnId).ConfigureAwait(false);
        if (repair["applied"].AsMap() is not { } applied)
        {
            throw new AcceptanceException($"recovery-repair: expected an applied result, got {repair.ToJson()}");
        }

        var job = applied["observed"].AsMap()?["job"].AsMap();
        if (job?["pawnId"].AsString() != pawnId || job["jobDef"].AsString() != "Repair")
        {
            throw new AcceptanceException($"recovery-repair: unexpected applied repair evidence: {job.ToJson()}");
        }

        if (job["targetA"].AsMap()?["thingId"].AsString() != wallId)
        {
            throw new AcceptanceException($"recovery-repair: unexpected repair target: {job.ToJson()}");
        }

        var replay = await Apply("recovery-repair", pawnId).ConfigureAwait(false);
        if (!Dyn.DeepEqual(replay, repair))
        {
            throw new AcceptanceException($"recovery-repair: resent key changed its result: {repair.ToJson()} then {replay.ToJson()}");
        }

        // Run real game time until the wall is back to full HitPoints.
        for (var advanced = 0; ; advanced += 300)
        {
            if (advanced >= 3 * Wait.TicksPerDay)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"wall was not repaired within {advanced} ticks"));
            }

            _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
            var (afterHitPoints, afterMax) = await WallState(string.Create(CultureInfo.InvariantCulture, $"wall-{advanced}")).ConfigureAwait(false);
            if (afterHitPoints >= afterMax)
            {
                report["wall_after_hit_points"] = afterHitPoints;
                break;
            }
        }

        report["wall_repaired"] = true;
    }
}
