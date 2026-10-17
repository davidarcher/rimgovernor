using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>tools/startcost</c> (Go <c>start_cost.go</c>): a diagnostic no tier runs (the <c>tools/</c> prefix) that measures
/// what a new-colony start costs and proves a pinned spec reproduces. Each start needs a fresh main menu, so every row
/// opens and retires its own game, and a failed row is recorded and the sweep goes on.
/// </summary>
internal static class ToolsStartCost
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "tools/startcost",
        Tier = CaseTier.Off,
        Scope = "Diagnostic (#2029): start time by planet coverage and map size, team-policy reroll cost by colonist count and seed, "
            + "and the tribal-8 spec started twice giving the same colonists, tile and map digest. Reported under start_cost; gates nothing.",
        Start = new OwnedStart(),
        Reason = "each start needs a fresh main menu process, so every row opens and retires its own game",
        NoKeep = true,
        Budget = TimeSpan.FromMinutes(90),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    /// <summary>The tribal-8 rules at a size: LostTribe, quiet, the baseline's biome preference widened so a small planet still offers a tile.</summary>
    private static JsonObject Spec(string seed, int count, int mapSize, double coverage, string saveName) => new()
    {
        ["scenario"] = "LostTribe",
        ["colonistCount"] = count,
        ["seed"] = seed,
        ["biomes"] = new JsonArray("TemperateForest", "BorealForest", "TemperateSwamp", "AridShrubland", "Tundra"),
        ["difficulty"] = "Medium",
        ["storyteller"] = ScenarioStarts.QuietStorytellerDef,
        ["worldTemperature"] = "LittleBitColder",
        ["mapSize"] = mapSize,
        ["planetCoverage"] = coverage,
        ["saveName"] = saveName,
    };

    private sealed record Read(IReadOnlyList<LifecycleNewColonyTeam.TeamPawn> Team, int Tile, string Hash)
    {
        internal string Digest() => $"tile={Tile} map={Hash} team={LifecycleNewColonyTeam.Digest(Team)}";
    }

    private static Config CoreOnly(Config cfg) => new()
    {
        Root = cfg.Root,
        Output = cfg.Output,
        Headless = cfg.Headless,
        Graphics = cfg.Graphics,
        GameId = cfg.GameId,
        Timeout = cfg.Timeout,
        Configuration = cfg.Configuration,
        Expansions = [],
        FixtureOps = cfg.FixtureOps,
        QuietWorld = cfg.QuietWorld,
        KeepLoaded = cfg.KeepLoaded,
        Resumed = cfg.Resumed,
        ServiceProfile = cfg.ServiceProfile,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var cfg = s.Config;
        var rows = new JsonObject();
        var failures = new List<string>();
        s.Report["start_cost"] = rows;

        // core runs on a Core-only profile, as the baseline is generated.
        async Task<Read?> RunOn(Config config, string name, JsonObject spec)
        {
            var row = new JsonObject
            {
                ["spec"] = spec.DeepClone(),
                ["expansions"] = config.Expansions is null ? null : NewColonyKit.Strings(config.Expansions),
            };
            rows[name] = row;
            try
            {
                return await RowAsync(config, name, spec, row, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                row["error"] = e.Message;
                return null;
            }
        }

        Task<Read?> Run(string name, JsonObject spec) => RunOn(cfg, name, spec);

        // RIMGOVERNOR_STARTCOST_ONLY=repro skips the sweep and runs only the reproducibility starts.
        var sweep = Environment.GetEnvironmentVariable("RIMGOVERNOR_STARTCOST_ONLY") != "repro";
        if (sweep)
        {
            foreach (var coverage in new[] { 0.05, 0.3, 0.5, 1.0 })
            {
                foreach (var size in new[] { 150, 250, 350 })
                {
                    var name = string.Create(CultureInfo.InvariantCulture, $"grid-cov{coverage:F2}-size{size}");
                    _ = await Run(name, Spec("startcost-grid", 8, size, coverage, name)).ConfigureAwait(false);
                }
            }

            // Reroll cost by colonist count, then by seed, on the small world so the rolling phase dominates.
            foreach (var count in new[] { 3, 8, 10 })
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"count-{count}");
                _ = await Run(name, Spec("startcost-count", count, 150, 0.05, name)).ConfigureAwait(false);
            }

            for (var i = 1; i <= 6; i++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"seed-{i}");
                _ = await Run(name, Spec(string.Create(CultureInfo.InvariantCulture, $"startcost-seed-{i}"), 8, 150, 0.05, name)).ConfigureAwait(false);
            }
        }

        // Reproducibility: the baseline spec and two more seeds, each twice under one save name, on Core-only and on all DLC.
        var baseline = Baselines.BaselineStart;
        foreach (var (profile, config) in new[] { ("core", CoreOnly(cfg)), ("dlc", cfg) })
        {
            // The profile on disk is shared, so each profile writes its own ModsConfig before its rows run (core rows
            // first, then all DLC).
            try
            {
                config.PrepareConfig();
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"prepare the {profile} profile: {e.Message}", e);
            }

            string[] seeds = [baseline.Seed, "startcost-repro-2", "startcost-repro-3"];
            for (var i = 0; i < seeds.Length; i++)
            {
                var tag = string.Create(CultureInfo.InvariantCulture, $"{profile}-s{i + 1}");
                JsonObject SpecFor(string save) => Spec(seeds[i], baseline.Count, baseline.Size.MapSize, baseline.Size.PlanetCoverage, save);
                var first = await RunOn(config, $"repro-{tag}-first", SpecFor($"startcost-repro-{tag}-a")).ConfigureAwait(false);
                var second = await RunOn(config, $"repro-{tag}-second", SpecFor($"startcost-repro-{tag}-b")).ConfigureAwait(false);
                if (first is null || second is null)
                {
                    throw new AcceptanceException($"the {tag} reproducibility starts did not both finish (see start_cost)");
                }

                var teamSame = LifecycleNewColonyTeam.Digest(first.Team) == LifecycleNewColonyTeam.Digest(second.Team) && first.Tile == second.Tile;
                rows[$"repro_{tag}_same"] = new JsonObject { ["colonists_and_tile"] = teamSame, ["map"] = first.Hash == second.Hash };
                if (first.Digest() != second.Digest())
                {
                    failures.Add($"{tag}: the same spec twice gave different colonies (colonists+tile same: {(teamSame ? "true" : "false")}, map same: {(first.Hash == second.Hash ? "true" : "false")})");
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", failures));
        }
    }

    /// <summary>Opens a game at the main menu, times one start of the spec and reads what a pinned spec must reproduce.</summary>
    private static async Task<Read> RowAsync(Config cfg, string name, JsonObject spec, JsonObject row, CancellationToken cancellationToken)
    {
        Read? read = null;
        await NewColonyKit.MenuAsync(
            cfg,
            null,
            name,
            [],
            async (h, _) =>
            {
                var request = NewColony.NewColonyRequest($"startcost-{name}-{LifecycleCases.Stamp()}", spec, TimeSpan.FromMinutes(25));
                NewColonyRun? run = null;
                AcceptanceException? failure = null;
                try
                {
                    run = await NewColony.RunNewColonyAsync(h, request, name, cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    failure = e;
                }

                if (failure is not null || run is null)
                {
                    throw failure ?? new AcceptanceException($"{name}: no start result");
                }

                row["phases_ms"] = new JsonObject([.. run.PhaseMs.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value)))]);
                row["rerolls"] = run.Rerolls;
                row["total_ms"] = run.TotalMs;
                if (run.PhaseMs.TryGetValue("ROLLING_COLONISTS", out var rolled) && run.Rerolls > 0
                    && PhaseAfter(run, "ROLLING_COLONISTS") is { } next && next > rolled)
                {
                    row["ms_per_reroll"] = (double)(next - rolled) / run.Rerolls;
                }

                var count = (int)spec["colonistCount"].AsNumber();
                var team = await LifecycleNewColonyTeam.ReadAsync(h, name, count, cancellationToken).ConfigureAwait(false);
                var save = cfg.ProfileSave(spec["saveName"].AsString());
                var hash = NewColony.SaveMapHash(save);
                var tile = NewColony.SaveStartingTile(save);
                read = new Read(team, tile, hash);
                row["tile"] = tile;
                row["map_digest"] = hash;
                row["team"] = NewColonyKit.Strings(LifecycleNewColonyTeam.Digest(team).Split(';'));
            },
            cancellationToken).ConfigureAwait(false);
        return read ?? throw new AcceptanceException($"{name}: no start result");
    }

    /// <summary>The start of the phase following <paramref name="name"/> in wire order, when the poll saw it.</summary>
    private static long? PhaseAfter(NewColonyRun run, string name)
    {
        var seen = false;
        foreach (var phase in NewColony.NewColonyPhaseOrder)
        {
            if (seen)
            {
                if (run.PhaseMs.TryGetValue(phase, out var ms))
                {
                    return ms;
                }

                continue;
            }

            seen = phase == name;
        }

        return null;
    }
}
