using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/upgrade</c> (Go <c>upgrade.go</c>): the typed wall-upgrade census behind
/// <c>rimgovernor/observations_list_wall_upgrade_sites</c> on a loaded save: the cleanup census (no target) is complete, and for every
/// colonist wall each per-target replacement row names the target with its CAS token, a normal, both side supports, its backup cells and
/// priced stone materials. At least one wall must yield a site or the run is vacuous. A fixture build's test/lighting_prepare first builds
/// an enclosed roofed room so the census has colonist walls to read; the stock saves hold none.
/// </summary>
internal static class WallUpgrade
{
    /// <summary>Bounds the colonist walls cross-checked per target (the first N by id).</summary>
    private const int MaxWalls = 64;

    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/upgrade",
        Scope = "Typed wall-upgrade site census: complete cleanup rows and, per colonist wall, complete replacement sites with supports, backup cells and priced stone materials; read-only, no designation or construction.",

        // The save carries its own expansion list (the runner enables them); the lighting fixture builds the walls on top of it.
        Start = new FixtureStart { Op = "test/lighting_prepare", On = new LabStart() },
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        if (!Dyn.Contains(s.Names, "rimgovernor/observations_list_wall_upgrade_sites"))
        {
            throw new AcceptanceException("missing rimgovernor/observations_list_wall_upgrade_sites in discovery");
        }

        var identityReply = await h.WireAsync("identity-before", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, before) = Harness.Outcome(identityReply, "loaded");
        var beforeContext = before["context"];
        var identity = beforeContext.Member("identity");
        var scope = WallCensus.Scope(identity);

        var walls = await WallCensus.ColonistWallsAsync(h, scope, "walls", cancellationToken).ConfigureAwait(false);
        if (walls.Count == 0)
        {
            throw new AcceptanceException("save has no colonist wall; the census assertion would be vacuous");
        }

        report["colonist_walls"] = walls.Count;

        var cleanupReply = await h.WireAsync("cleanup-census", "observations_list_wall_upgrade_sites", new JsonObject { ["scope"] = scope.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var cleanup = WallCensus.Observed("cleanup census", cleanupReply);
        if (!Dyn.DeepEqual(cleanup["context"], beforeContext))
        {
            throw new AcceptanceException("cleanup census context drifted mid-run");
        }

        var cleanupRows = cleanup["sites"].AsSlice();
        foreach (var raw in cleanupRows)
        {
            var row = raw.AsMap() ?? [];
            if (!row.ContainsKey("replacement") || row["completedBackups"].AsSlice().Count == 0)
            {
                throw new AcceptanceException($"cleanup row without a permanent replacement and remaining backups: {row["target"].ToJson()}");
            }
        }

        report["cleanup_sites"] = cleanupRows.Count;

        if (walls.Count > MaxWalls)
        {
            walls = walls.GetRange(0, MaxWalls);
        }

        var sitesFound = 0;
        var wallsWithSites = 0;
        foreach (var wall in walls)
        {
            var typedReply = await h.WireAsync("typed-" + wall, "observations_list_wall_upgrade_sites", new JsonObject { ["scope"] = scope.DeepClone(), ["targetId"] = wall }, cancellationToken).ConfigureAwait(false);
            var observed = WallCensus.Observed(wall, typedReply);
            var rows = observed["sites"].AsSlice();
            CheckSites(wall, rows);
            if (rows.Count > 0)
            {
                wallsWithSites++;
                sitesFound += rows.Count;
            }
        }

        if (sitesFound == 0)
        {
            throw new AcceptanceException("no colonist wall yielded a replacement site; the geometry assertion is vacuous");
        }

        report["walls_checked"] = walls.Count;
        report["walls_with_sites"] = wallsWithSites;
        report["replacement_sites"] = sitesFound;

        foreach (var (label, targetId, code) in new[]
        {
            ("blank-target", " ", "FAILURE_CODE_INVALID_REQUEST"),
            ("unknown-target", "Thing_NoSuchWall0", "FAILURE_CODE_NOT_FOUND"),
        })
        {
            var reply = await h.WireAsync(label, "observations_list_wall_upgrade_sites", new JsonObject { ["scope"] = scope.DeepClone(), ["targetId"] = targetId }, cancellationToken).ConfigureAwait(false);
            var got = WallCensus.FailureCode(reply);
            if (got != code)
            {
                throw new AcceptanceException($"{label}: expected {code}, got \"{got}\"");
            }
        }

        var identityAfterReply = await h.WireAsync("identity-after", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, after) = Harness.Outcome(identityAfterReply, "loaded");
        if (!Dyn.DeepEqual(after["context"], beforeContext))
        {
            throw new AcceptanceException("identity/tick changed during a read-only pass");
        }
    }

    /// <summary>Asserts every per-target row is a complete replacement site.</summary>
    private static void CheckSites(string wall, IReadOnlyList<JsonNode?> rows)
    {
        foreach (var raw in rows)
        {
            var row = raw.AsMap() ?? [];
            if (row["target"].Member("id").AsString() != wall || row["original"].Member("id").AsString() != wall || row["targetSnapshot"].Member("token").AsString().Length == 0)
            {
                throw new AcceptanceException($"{wall}: replacement row must target the original wall with a CAS token");
            }

            if (row["targetPresent"].AsBool() != true)
            {
                throw new AcceptanceException($"{wall}: target reported absent");
            }

            var normal = row["normal"];
            var nx = normal.Member("x").AsNumber();
            var nz = normal.Member("z").AsNumber();
            if (nx == 0 && nz == 0)
            {
                throw new AcceptanceException($"{wall}: site without a normal");
            }

            if (row["leftSupport"].Member("id").AsString().Length == 0 || row["rightSupport"].Member("id").AsString().Length == 0)
            {
                throw new AcceptanceException($"{wall}: site without both supports: {row.ToJson()}");
            }

            // A corner (diagonal normal) is replaced in place and has no backup cells; a straight site has its three exterior backups.
            var corner = nx != 0 && nz != 0;
            var backups = row["backupCells"].AsSlice().Count;
            if ((corner && backups != 0) || (!corner && backups != 3))
            {
                throw new AcceptanceException($"{wall}: site with {backups} backup cells for normal {normal.ToJson()}: {row.ToJson()}");
            }

            var options = row["replacementMaterials"].AsSlice();
            if (options.Count == 0)
            {
                throw new AcceptanceException($"{wall}: site without replacement materials");
            }

            foreach (var option in options)
            {
                var costs = option.Member("costs").AsSlice();
                if (option.Member("stuff").AsString().Length == 0 || costs.Count == 0)
                {
                    throw new AcceptanceException($"{wall}: material without a stuff or costs: {option.ToJson()}");
                }

                foreach (var cost in costs)
                {
                    if (cost.Member("defName").AsString().Length == 0 || cost.Member("units").AsNumber() <= 0)
                    {
                        throw new AcceptanceException($"{wall}: material {option.Member("stuff").ToJson()} has an unpriced cost {cost.ToJson()}");
                    }
                }
            }
        }
    }
}
