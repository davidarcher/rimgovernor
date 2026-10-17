using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/removal</c> (Go <c>removal.go</c>): the wall_upgrade-guarded Designate on Actions/Apply on a loaded save.
/// NativeWallRemovalOperations resolves the guarded demolition site from the wall at the named cell, refuses a wall without completed stone
/// backups, applies the original's demolition once same-stuff stone backups stand in its backup cells, and a real supervised native
/// deconstruct job clears the wall (the wall gone from the building census, which is what Go's dependency gate reads). A standing stone
/// permanent wall then makes the backups cleanup sites: one backup is demolished the same way. A resent key returns its first result, and a
/// cleared cell applies again. A LightingFixture + UpkeepFixture build supplies test/lighting_prepare (an enclosed roofed room of colonist
/// walls) and test/stone_walls_spawn (finished stone walls standing in for completed backup and permanent construction).
/// </summary>
internal static class WallRemoval
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/removal",
        Scope = "Native wall_upgrade-guarded Designate: site resolution from the wall at a cell, refusal without " +
            "backups, guarded demolition of the original and of a backup by real supervised native deconstruct jobs, resend idempotency.",
        Start = new FixtureStart { Op = "test/lighting_prepare", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private sealed class Site
    {
        public string Wall { get; init; } = "";

        public string Stuff { get; init; } = "";

        public double X { get; init; }

        public double Z { get; init; }

        public double Nx { get; init; }

        public double Nz { get; init; }

        /// <summary>"x,z" in native backup order.</summary>
        public List<string> Cells { get; } = [];
    }

    private static JsonObject RowForNormal(IReadOnlyList<JsonNode?> rows, double nx, double nz)
    {
        JsonObject? found = null;
        foreach (var raw in rows)
        {
            var row = raw.AsMap() ?? [];
            var normal = row["normal"];
            if (normal.Member("x").AsNumber() != nx || normal.Member("z").AsNumber() != nz)
            {
                continue;
            }

            found = found is null ? row : throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"several rows share normal ({nx},{nz})"));
        }

        return found ?? throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"no row with normal ({nx},{nz}) among {rows.Count}"));
    }

    /// <summary>Reads one wall's cell from the building census; the site row names its wall by Ref only.</summary>
    private static async Task<JsonNode?> WallPositionAsync(Harness h, JsonObject scope, string wall, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync("position-" + wall, "observations_list_buildings", new JsonObject { ["scope"] = scope.DeepClone(), ["ids"] = new JsonArray(wall) }, cancellationToken).ConfigureAwait(false);
        var observed = WallCensus.Observed("position-" + wall, reply);
        foreach (var raw in observed["buildings"].AsSlice())
        {
            var building = raw.Member("building");
            if (building.Member("id").AsString() == wall)
            {
                return building.Member("position");
            }
        }

        throw new AcceptanceException($"position-{wall}: wall absent from the building census");
    }

    private static string CellText(JsonNode? cell) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)cell.Member("x").AsNumber()},{(int)cell.Member("z").AsNumber()}");

    private static JsonNode? ApplyEffect(JsonObject result) =>
        result["applied"].Member("applied").Member("observed").Member("wall");

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        // The save carries its own expansion list (the runner enables them); the lighting fixture builds the colonist walls on top of it.
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        foreach (var name in new[] { "rimgovernor/operations_apply", "rimgovernor/observations_list_wall_upgrade_sites", "test/stone_walls_spawn" })
        {
            if (!Dyn.Contains(s.Names, name))
            {
                throw new AcceptanceException($"missing {name} in discovery; rebuild the native mod with -Fixture LightingFixture,UpkeepFixture");
            }
        }

        var clock = new ScenarioClock(h, identity, report);
        _ = await h.CallAsync("initial-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var scope = WallCensus.Scope(identity);

        async Task<IReadOnlyList<JsonNode?>> Census(string label, string target)
        {
            var request = new JsonObject { ["scope"] = scope.DeepClone() };
            if (target.Length > 0)
            {
                request["targetId"] = target;
            }

            var reply = await h.WireAsync(label, "observations_list_wall_upgrade_sites", request, cancellationToken).ConfigureAwait(false);
            return WallCensus.Observed(label, reply)["sites"].AsSlice();
        }

        var walls = await WallCensus.ColonistWallsAsync(h, scope, "walls", cancellationToken).ConfigureAwait(false);
        if (walls.Count == 0)
        {
            throw new AcceptanceException("fixture produced no colonist wall");
        }

        Site? chosen = null;
        foreach (var wall in walls)
        {
            var rows = await Census("candidates-" + wall, wall).ConfigureAwait(false);
            foreach (var raw in rows)
            {
                var row = raw.AsMap() ?? [];
                var nx = row["normal"].Member("x").AsNumber();
                var nz = row["normal"].Member("z").AsNumber();
                var cells = row["backupCells"].AsSlice();
                if ((nx != 0 && nz != 0) || row["blocker"].AsString().Length != 0 || cells.Count != 3 || row["completedBackups"].AsSlice().Count != 0)
                {
                    continue;
                }

                var materials = row["replacementMaterials"].AsSlice();
                if (materials.Count == 0)
                {
                    continue;
                }

                var position = await WallPositionAsync(h, scope, wall, cancellationToken).ConfigureAwait(false);
                chosen = new Site
                {
                    Wall = wall,
                    Stuff = materials[0].Member("stuff").AsString(),
                    X = position.Member("x").AsNumber(),
                    Z = position.Member("z").AsNumber(),
                    Nx = nx,
                    Nz = nz,
                };
                chosen.Cells.AddRange(cells.Select(CellText));
                break;
            }

            if (chosen is not null)
            {
                break;
            }
        }

        if (chosen is null)
        {
            throw new AcceptanceException("no colonist wall offers a straight replacement site with three open backup cells");
        }

        var site = chosen;
        report["site_wall"] =site.Wall;
        report["site_stuff"] = site.Stuff;
        report["site_backup_cells"] = string.Join(';', site.Cells);

        // Acquire sets Auto mode at the current generation (no lease handshake); it is repeated before every dispatch that follows a supervised
        // run, since player-visible activity may bump the generation. The clock keeps the grant, so its runs start at the same generation.
        Task Acquire(string label) => clock.AcquireAsync(label, cancellationToken);

        // Apply sends one wall_upgrade-guarded Designate for the wall at cell under key and returns its ActionResult.
        async Task<JsonObject> Apply(string label, string key, JsonObject cell, string expected)
        {
            var intent = new JsonObject { ["designation"] = "THING_DESIGNATION_DECONSTRUCT", ["cell"] = cell.DeepClone(), ["guard"] = "DESIGNATION_GUARD_WALL_UPGRADE" };
            if (expected.Length > 0)
            {
                intent["target"] = new JsonObject { ["id"] = expected };
            }

            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject { ["identity"] = identity.DeepClone(), ["actions"] = new JsonArray(new JsonObject { ["key"] = key, ["designate"] = intent }) },
                cancellationToken).ConfigureAwait(false);
            var results = reply["results"].AsSlice();
            return results.Count == 1 ? results[0].AsMap() ?? [] : throw new AcceptanceException($"{label}: expected one result: {reply.ToJson()}");
        }

        async Task ExpectRefused(string label, string key, JsonObject cell, string expected, params string[] codes)
        {
            var result = await Apply(label, key, cell, expected).ConfigureAwait(false);
            var refusal = result["refused"].AsMap() ?? throw new AcceptanceException($"{label}: expected a refusal, got {result.ToJson()}");
            var code = refusal["code"].AsString();
            if (!codes.Contains(code, StringComparer.Ordinal))
            {
                throw new AcceptanceException($"{label}: expected [{string.Join(' ', codes)}], got \"{code}\" ({refusal["reason"].AsString()})");
            }
        }

        // Execute applies one guarded removal and checks its applied evidence.
        async Task<JsonObject> Execute(string label, string key, string wall, JsonObject cell)
        {
            await Acquire(label + "-acquire").ConfigureAwait(false);
            var result = await Apply(label, key, cell, wall).ConfigureAwait(false);
            if (result["applied"].AsMap() is null)
            {
                throw new AcceptanceException($"{label}: expected an applied outcome, got {result.ToJson()}");
            }

            var effect = ApplyEffect(result);
            var siteEvidence = effect.Member("site");
            if (effect.Member("targetId").AsString() != wall
                || effect.Member("removalId").AsString().Length == 0
                || effect.Member("workerIds").AsSlice().Count == 0
                || siteEvidence.Member("entityId").AsString() != wall
                || siteEvidence.Member("beforeToken").AsString().Length == 0
                || siteEvidence.Member("afterToken").AsString().Length == 0)
            {
                throw new AcceptanceException($"{label}: unexpected wall effect: {effect.ToJson()}");
            }

            if (effect.Member("demolitionObserved").AsBool() == true)
            {
                throw new AcceptanceException($"{label}: demolition cannot be observed at admission");
            }

            return result;
        }

        // RunUntilGone drives the supervised native clock, heartbeating it and running it again after each hold, until the building census no longer
        // lists wall within ticks of game time. The signature carries the game tick, so a game that stops ticking stalls and one that keeps
        // ticking without the demolition spends the tick allowance.
        async Task RunUntilGone(string label, string wall, ulong ticks)
        {
            _ = await clock.RunAsync("Fast", cancellationToken).ConfigureAwait(false);
            JsonObject state = [];
            var polls = 0;
            ulong latest = 0;
            try
            {
                await new Wait
                {
                    Stall = Wait.StallBudget(),
                    Ticks = ticks,
                    Tick = async ct =>
                    {
                        latest = await h.TickAsync(ct).ConfigureAwait(false);
                        return latest;
                    },
                }.WaitProgressAsync(
                    async ct =>
                    {
                        polls++;
                        await clock.BeatIfDueAsync(ct).ConfigureAwait(false);
                        state = await clock.StatusAsync(ct).ConfigureAwait(false);
                        var current = await WallCensus.ColonistWallsAsync(h, scope, $"{label}-walls-{CaseSupport.Tag(polls)}", ct).ConfigureAwait(false);
                        if (!current.Contains(wall, StringComparer.Ordinal))
                        {
                            return (string.Empty, true);
                        }

                        if (state["active"].AsBool() != true)
                        {
                            clock.Hold = string.Empty;
                            _ = await clock.RunAsync("Fast", ct).ConfigureAwait(false);
                        }

                        return (Wait.Signature(latest, state["stopReason"].ToJson()), false);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{label}: {e.Message}", e);
            }

            if (state["active"].AsBool() == true)
            {
                _ = await clock.HoldAsync("REVIEW", "removal run finished", cancellationToken).ConfigureAwait(false);
            }
        }

        async Task<List<string>> Spawn(string label, string cells)
        {
            var reply = await h.CallAsync(label, "test/stone_walls_spawn", new JsonObject { ["cells"] = cells, ["stuff"] = site.Stuff }, cancellationToken).ConfigureAwait(false);
            if (reply["success"].AsBool() != true)
            {
                throw new AcceptanceException($"{label}: {reply.ToJson()}");
            }

            return [.. reply["walls"].AsSlice().Select(raw => raw.AsString())];
        }

        // Refusals before any backup stands: the site is not demolition ready.
        JsonObject WallCell() => CaseSupport.Cell((int)site.X, (int)site.Z);
        await Acquire("acquire").ConfigureAwait(false);
        await ExpectRefused("apply-without-backups", "wall-early", WallCell(), site.Wall, "FAILURE_CODE_INVALID_REQUEST").ConfigureAwait(false);
        await ExpectRefused("apply-other-wall", "wall-other", WallCell(), "Thing_NoSuchWall0", "FAILURE_CODE_STALE_IDENTITY").ConfigureAwait(false);

        // Completed backups: the target row now lists them and admits demolition.
        var backups = await Spawn("spawn-backups", string.Join(';', site.Cells)).ConfigureAwait(false);
        if (backups.Count != 3)
        {
            throw new AcceptanceException($"spawn-backups: expected three backups, got [{string.Join(' ', backups)}]");
        }

        report["backups"] = new JsonArray([.. backups.Select(b => (JsonNode?)JsonValue.Create(b))]);

        // The guard admits the original's demolition only while stock covers the permanent wall it will be rebuilt from; the fixture supplies that stock.
        var stocked = await h.CallAsync("stock-blocks", "test/wall_material_loss", new JsonObject { ["material"] = site.Stuff, ["restore"] = 60 }, cancellationToken).ConfigureAwait(false);
        if (stocked["success"].AsBool() != true)
        {
            throw new AcceptanceException($"stock-blocks: {stocked.ToJson()}");
        }

        var ready = await Census("ready-census", site.Wall).ConfigureAwait(false);
        JsonObject readyRow;
        try
        {
            readyRow = RowForNormal(ready, site.Nx, site.Nz);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"ready-census: {e.Message}", e);
        }

        if (readyRow["completedBackups"].AsSlice().Count != 3 || readyRow["blocker"].AsString().Length != 0)
        {
            throw new AcceptanceException($"ready-census: expected three completed backups and no blocker, got {readyRow.ToJson()}");
        }

        if (readyRow["designated"].AsBool() == true)
        {
            throw new AcceptanceException("ready-census: wall already designated before dispatch");
        }

        _ = await h.CallAsync("standing-demolition", "test/deconstruct_target", new JsonObject { ["target"] = site.Wall, ["action"] = "replace" }, cancellationToken).ConfigureAwait(false);
        var standing = await Census("standing-designation", site.Wall).ConfigureAwait(false);
        var standingRow = RowForNormal(standing, site.Nx, site.Nz);
        if (standingRow["designated"].AsBool() != true || standingRow["removalId"].AsString().Length != 0)
        {
            throw new AcceptanceException($"standing demolition designation not observed: {standingRow.ToJson()}");
        }

        var demolishResult = await Execute("apply-adopt-demolition", "wall-demolish", site.Wall, WallCell()).ConfigureAwait(false);
        var removalId = ApplyEffect(demolishResult).Member("removalId").AsString();
        var designatedRows = await Census("designated-census", site.Wall).ConfigureAwait(false);
        var designatedRow = RowForNormal(designatedRows, site.Nx, site.Nz);
        if (designatedRow["designated"].AsBool() != true || designatedRow["removalId"].AsString() != removalId)
        {
            throw new AcceptanceException($"designated-census: expected the ledger's designation {removalId}, got {designatedRow.ToJson()}");
        }

        var replay = await Apply("replay-demolish", "wall-demolish", WallCell(), site.Wall).ConfigureAwait(false);
        if (!Dyn.DeepEqual(replay, demolishResult))
        {
            throw new AcceptanceException("replay-demolish: a resent key returned a different result");
        }

        var again = await Apply("reapply-demolish", "wall-demolish-again", WallCell(), site.Wall).ConfigureAwait(false);
        if (ApplyEffect(again).Member("removalId").AsString() != removalId)
        {
            throw new AcceptanceException($"reapply-demolish: a new key must apply again on the pending removal, got {again.ToJson()}");
        }

        await RunUntilGone("demolish", site.Wall, 2UL * Wait.TicksPerDay).ConfigureAwait(false);
        var cleared = await Apply("reapply-cleared", "wall-demolish-cleared", WallCell(), site.Wall).ConfigureAwait(false);
        if (cleared["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"reapply-cleared: a cleared cell must apply again, got {cleared.ToJson()}");
        }

        report["original_demolished"] = true;

        // A standing permanent wall turns the backups into cleanup sites.
        var permanent = await Spawn("spawn-permanent", string.Create(CultureInfo.InvariantCulture, $"{(int)site.X},{(int)site.Z}")).ConfigureAwait(false);
        var cleanupRows = await Census("cleanup-census", string.Empty).ConfigureAwait(false);
        JsonObject cleanupRow;
        try
        {
            cleanupRow = RowForNormal(cleanupRows, site.Nx, site.Nz);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"cleanup-census: {e.Message}", e);
        }

        var firstBackup = cleanupRow["target"].Member("id").AsString();
        if (!backups.Contains(firstBackup, StringComparer.Ordinal)
            || permanent.Count == 0
            || cleanupRow["replacement"].Member("id").AsString() != permanent[0]
            || cleanupRow["completedBackups"].AsSlice().Count != 3)
        {
            throw new AcceptanceException($"cleanup-census: expected a backup target and the permanent replacement, got {cleanupRow.ToJson()}");
        }

        var backupPosition = await WallPositionAsync(h, scope, firstBackup, cancellationToken).ConfigureAwait(false);
        var backupCell = CaseSupport.Cell((int)backupPosition.Member("x").AsNumber(), (int)backupPosition.Member("z").AsNumber());
        _ = await Execute("apply-cleanup", "wall-cleanup", firstBackup, backupCell).ConfigureAwait(false);
        await RunUntilGone("cleanup", firstBackup, 2UL * Wait.TicksPerDay).ConfigureAwait(false);
        var afterCleanup = await Census("after-cleanup-census", string.Empty).ConfigureAwait(false);
        JsonObject afterCleanupRow;
        try
        {
            afterCleanupRow = RowForNormal(afterCleanup, site.Nx, site.Nz);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"after-cleanup-census: {e.Message}", e);
        }

        var secondBackup = afterCleanupRow["target"].Member("id").AsString();
        if (afterCleanupRow["completedBackups"].AsSlice().Count != 2 || secondBackup == firstBackup || !backups.Contains(secondBackup, StringComparer.Ordinal))
        {
            throw new AcceptanceException($"after-cleanup-census: expected two remaining backups naming the next, got {afterCleanupRow.ToJson()}");
        }

        report["backup_demolished"] = firstBackup;
    }
}
