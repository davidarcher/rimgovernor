using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>combatlab/mirror</c> (Go <c>mirror.go</c>).</summary>
internal static class CombatLabMirror
{
    // mirrorStepTicks is one synchronous lab step between frames: the stream's capture period.
    internal const int StepTicks = 60;

    // mirrorFightTicks bounds the lab-ranged firefight until a pawn goes down (the case budget is 5,000 ticks).
    internal const int FightTicks = 4800;

    // geometryBudgetMs is the main-thread budget of one geometry read at the caps.
    internal const double GeometryBudgetMs = 50.0;

    internal static AcceptanceCase Case() => new()
    {
        Name = "combatlab/mirror",
        Scope = "Combat frame sections (#851, #858) and combat.geometry on lab-ranged: a snapshot frame's combat_pawns lists the 4 riflemen and 4 raiders with rifles; "
            + "combat.geometry gives a sandbagged rifleman cover against the raider north of him, an open cell none, line of fire to every raider, a colonist in the path from the cell behind a rifleman, "
            + "path ticks for a named pawn, and a read at 64 cells x 8 pawns under 50 ms of main thread; proposing cover_behind_line on the sandbags returns every cell behind them, and a firing_cells proposal at the radius cap fills 64 cells within budget (#871); the firefight's first downing or death arrives as a combat_events row and, in the same frame, "
            + "the pawn's row (downed, stamped with the event's watermark) or its absence; then lab-pods' center drop arrives as a hostile_arrived row with strategy pods, "
            + "landing cells and an open tick by which every raider is out of its pod (#870).",
        Start = new LabStart { Colonists = 4 },
        RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool],
        QuietWorld = true,
        Budget = AcceptanceCase.LabBudget,
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        await RunWindowsAsync(s, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunWindowsAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var staged = await CombatStage.StageAsync(h, "lab-ranged", cancellationToken).ConfigureAwait(false);
        var identity = s.Identity;
        var report = s.Report;

        // Geometry reads the staged layout before any tick: the undrafted colonists wander off their cells once the
        // clock runs.
        await GeometryAsync(h, identity, staged, report, cancellationToken).ConfigureAwait(false);
        var frames = await CombatFrames.OpenAsync(h, identity, cancellationToken).ConfigureAwait(false);

        // One step so a frame is captured past the staging.
        var (_, tick) = await CombatStage.TickAsync(h, StepTicks, cancellationToken).ConfigureAwait(false);
        var state = await frames.NextAsync(tick, cancellationToken).ConfigureAwait(false);
        var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in state.Pawns)
        {
            rows[row["id"].AsString()] = row;
        }

        // The weapon's range and class are the def rows', not the frame's.
        var rifleFacts = await WeaponCatalog.RangedFactsAsync(h, identity, CombatFixtures.Rifle, cancellationToken).ConfigureAwait(false);
        if (!rifleFacts.Ranged || rifleFacts.Range <= 0)
        {
            throw new AcceptanceException($"catalog rows give {CombatFixtures.Rifle} no ranged verb: {{Ranged:{rifleFacts.Ranged} Range:{rifleFacts.Range.ToString(CultureInfo.InvariantCulture)}}}");
        }

        foreach (var id in staged.Colonists().Concat(staged.Hostiles()))
        {
            if (!rows.TryGetValue(id, out var row) || row["weapon"].AsString() != CombatFixtures.Rifle)
            {
                throw new AcceptanceException($"combat_pawns row for {id}: {(rows.TryGetValue(id, out var shown) ? shown.ToJson() : "<nil>")} (frame {CaseSupport.Tag(rows.Count)} rows)");
            }
        }

        report["frameRows"] = rows.Count;
        await FightAsync(h, frames, report, cancellationToken).ConfigureAwait(false);
        await DropPodsAsync(h, frames, report, cancellationToken).ConfigureAwait(false);
    }

    // dropPods stages lab-pods: the center drop's arrival is a combat_events hostile_arrived row with strategy pods, its
    // landing cells and an open tick after the arrival, and by that tick every raider is out.
    private static async Task DropPodsAsync(Harness h, CombatFrames frames, Report report, CancellationToken cancellationToken)
    {
        var staged = await CombatStage.StageAsync(h, "lab-pods", cancellationToken).ConfigureAwait(false);
        var (_, tick) = await CombatStage.TickAsync(h, StepTicks, cancellationToken).ConfigureAwait(false);
        var state = await frames.NextAsync(tick, cancellationToken).ConfigureAwait(false);
        JsonObject? row = null;
        foreach (var e in state.Events)
        {
            if (Geometry.DropPodArrival(e))
            {
                row = e;
            }
        }

        if (row is null)
        {
            throw new AcceptanceException($"lab-pods: no combat_events row with strategy pods in {CaseSupport.Tag(state.Events.Count)} frame events");
        }

        var at = row["at"].AsMap();
        var landing = row["landingCells"].AsSlice().Count;
        report["pods"] = new JsonObject
        {
            ["at"] = Geometry.WatermarkTick(at),
            ["openTick"] = row["openTick"].AsNumberOrZero(),
            ["landingCells"] = landing,
            ["arrival"] = row["defName"].AsString(),
        };
        var wait = (int)((long)row["openTick"].AsNumberOrZero() - Geometry.WatermarkTick(at)) + StepTicks;
        if (wait <= StepTicks || landing == 0)
        {
            throw new AcceptanceException($"lab-pods row without an open tick after its arrival or landing cells: {row.ToJson()}");
        }

        var (pawns, _) = await CombatStage.TickAsync(h, wait, cancellationToken).ConfigureAwait(false);
        foreach (var id in staged.Hostiles())
        {
            if (!pawns.ContainsKey(id))
            {
                throw new AcceptanceException($"lab-pods raider {id} still in its pod {CaseSupport.Tag(wait)} ticks after the arrival (open tick {CaseSupport.Tag((long)row["openTick"].AsNumberOrZero())})");
            }
        }
    }

    // geometry checks the lab-ranged answers and times a read at the caps.
    private static async Task GeometryAsync(Harness h, JsonObject identity, StagedFixture staged, Report report, CancellationToken cancellationToken)
    {
        // Spec order: colonist i then its raider straight north.
        LabPawn? rifleman = null;
        LabPawn? raider = null;
        foreach (var pawn in staged.Fixture.Pawns)
        {
            if (pawn.Side == CombatFixtures.Colonist && rifleman is null)
            {
                rifleman = pawn;
            }

            if (pawn.Side == CombatFixtures.Hostile && raider is null)
            {
                raider = pawn;
            }
        }

        rifleman ??= new LabPawn();
        raider ??= new LabPawn();
        var hostiles = staged.Hostiles();
        var colonists = staged.Colonists();
        var open = CaseSupport.Cell(rifleman.X - 12, rifleman.Z);
        var behind = CaseSupport.Cell(rifleman.X, rifleman.Z - 2);
        var request = Geometry.Ask(identity, [CaseSupport.Cell(rifleman.X, rifleman.Z), open, behind], hostiles.Take(1), colonists[1]);
        var reply = await Geometry.CallAsync(h, "combat-geometry", request, cancellationToken).ConfigureAwait(false);
        var g = Geometry.Observed(reply);
        try
        {
            Geometry.Validate(g, request);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"geometry reply {reply.ToJson()}: {e.Message}", e);
        }

        var cells = g!["cells"].AsSlice().Select(c => c.AsMap() ?? []).ToList();
        var sandbagged = cells[0]["lines"].AsSlice()[0].AsMap() ?? [];
        var bare = cells[1]["lines"].AsSlice()[0].AsMap() ?? [];
        var back = cells[2]["lines"].AsSlice()[0].AsMap() ?? [];
        report["geometry"] = new JsonObject
        {
            ["sandbaggedCover"] = sandbagged["cover"].AsNumberOrZero(),
            ["openCover"] = bare["cover"].AsNumberOrZero(),
            ["behindColonistInPath"] = back["colonistInPath"].AsBool() == true,
            ["pathTicks"] = new JsonArray(cells[0]["pathTicks"].AsNumberOrZero(), cells[1]["pathTicks"].AsNumberOrZero(), cells[2]["pathTicks"].AsNumberOrZero()),
        };
        if (sandbagged["cover"].AsNumberOrZero() <= 0 || bare["cover"].AsNumberOrZero() != 0 || sandbagged["lineOfFire"].AsBool() != true || bare["lineOfFire"].AsBool() != true
            || back["colonistInPath"].AsBool() != true || sandbagged["colonistInPath"].AsBool() == true)
        {
            throw new AcceptanceException($"geometry answers off lab-ranged's layout (raider {CaseSupport.Tag(raider.X)},{CaseSupport.Tag(raider.Z)}): {g.ToJson()}");
        }

        if (cells[1]["pathTicks"] is null || cells[1]["pathTicks"].AsNumberOrZero() <= 0)
        {
            throw new AcceptanceException($"no path ticks to the open cell: {cells[1].ToJson()}");
        }

        // The timing: every staged pawn as a target (only 4 are hostile, and the read does not care which side a target
        // is on) against the cells-cap of cells around the line, with path ticks.
        var targets = hostiles.Concat(colonists).ToList();
        var capCells = new List<JsonObject>();
        for (var dz = 0; capCells.Count < Geometry.MaxCells; dz++)
        {
            for (var dx = -8; dx < 8 && capCells.Count < Geometry.MaxCells; dx++)
            {
                capCells.Add(CaseSupport.Cell(rifleman.X + 3 + dx, rifleman.Z - 2 - dz));
            }
        }

        var samples = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var capRequest = Geometry.Ask(identity, capCells, targets, colonists[1]);
            var capReply = await Geometry.CallAsync(h, $"combat-geometry-cap-{CaseSupport.Tag(i)}", capRequest, cancellationToken).ConfigureAwait(false);
            try
            {
                Geometry.Validate(Geometry.Observed(capReply), capRequest);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"geometry at the caps {capReply["failure"].ToJson()}: {e.Message}", e);
            }

            samples.Add(Geometry.Observed(capReply)!["mainThreadMs"].AsNumberOrZero());
        }

        samples.Sort();
        var worst = samples[^1];
        var perPair = worst / (capCells.Count * targets.Count);
        var projected = perPair * Geometry.MaxCells * Geometry.MaxHostiles;
        report["geometryTiming"] = new JsonObject
        {
            ["cells"] = capCells.Count,
            ["targets"] = targets.Count,
            ["msSorted"] = new JsonArray([.. samples.Select(m => (JsonNode?)JsonValue.Create(m))]),
            ["worstMs"] = worst,
            ["projectedAtCapsMs"] = projected,
            ["caps"] = $"{CaseSupport.Tag(Geometry.MaxCells)}x{CaseSupport.Tag(Geometry.MaxHostiles)}",
        };
        if (projected > GeometryBudgetMs)
        {
            throw new AcceptanceException($"geometry at the caps projects {projected.ToString("0.0", CultureInfo.InvariantCulture)} ms (worst {worst.ToString("0.0", CultureInfo.InvariantCulture)} ms at {CaseSupport.Tag(capCells.Count)}x{CaseSupport.Tag(targets.Count)}), over {GeometryBudgetMs.ToString("0", CultureInfo.InvariantCulture)} ms");
        }

        await ProposeAsync(h, identity, staged, rifleman, report, cancellationToken).ConfigureAwait(false);
    }

    // propose: cover_behind_line on the sandbag line returns every cell behind it (the riflemen's row), and a
    // firing_cells proposal at the radius cap, 64 cells scored against every staged pawn, stays in budget.
    private static async Task ProposeAsync(Harness h, JsonObject identity, StagedFixture staged, LabPawn rifleman, Report report, CancellationToken cancellationToken)
    {
        var line = new JsonArray();
        var behind = new HashSet<(int, int)>();
        foreach (var th in staged.Fixture.Things)
        {
            if (th.Def == "Sandbags")
            {
                line.Add(CaseSupport.Cell(th.X, th.Z));
                _ = behind.Add((th.X, th.Z - 1));
            }
        }

        var request = Geometry.ProposeAsk(identity, Geometry.Role("coverBehindLine", new JsonObject { ["line"] = line }), staged.Hostiles(), "");
        var reply = await Geometry.CallAsync(h, "combat-geometry-propose-cover", request, cancellationToken).ConfigureAwait(false);
        var g = Geometry.Observed(reply);
        try
        {
            Geometry.Validate(g, request);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"propose cover_behind_line {reply["failure"].ToJson()}: {e.Message}", e);
        }

        var proposed = new JsonArray();
        var proposedText = new List<string>();
        foreach (var row in g!["proposed"].AsSlice())
        {
            var cell = row.AsMap()?["cell"].AsMap() ?? [];
            var key = ((int)cell["x"].AsNumberOrZero(), (int)cell["z"].AsNumberOrZero());
            proposed.Add(new JsonArray(key.Item1, key.Item2));
            proposedText.Add($"[{CaseSupport.Tag(key.Item1)} {CaseSupport.Tag(key.Item2)}]");
            _ = behind.Remove(key);
        }

        report["proposeCover"] = new JsonObject { ["cells"] = proposed, ["ms"] = g["mainThreadMs"].AsNumberOrZero() };
        if (behind.Count != 0)
        {
            var missed = string.Join(" ", behind.OrderBy(b => b.Item1).ThenBy(b => b.Item2).Select(b => $"[{CaseSupport.Tag(b.Item1)} {CaseSupport.Tag(b.Item2)}]"));
            throw new AcceptanceException($"cover_behind_line on the sandbags missed [{missed}]; proposed [{string.Join(" ", proposedText)}]");
        }

        var pawns = staged.Hostiles().Concat(staged.Colonists()).ToList();
        var targets = new JsonArray();
        foreach (var pawn in staged.Fixture.Pawns)
        {
            targets.Add(CaseSupport.Cell(pawn.X, pawn.Z));
        }

        var firing = Geometry.Role("firingCells", new JsonObject
        {
            ["targets"] = targets,
            ["from"] = CaseSupport.Cell(rifleman.X, rifleman.Z - 3),
            ["radius"] = Geometry.MaxRadius,
        });
        var samples = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var firingRequest = Geometry.ProposeAsk(identity, firing, pawns, staged.Colonists()[1]);
            var firingReply = await Geometry.CallAsync(h, $"combat-geometry-propose-cap-{CaseSupport.Tag(i)}", firingRequest, cancellationToken).ConfigureAwait(false);
            var observed = Geometry.Observed(firingReply);
            try
            {
                Geometry.Validate(observed, firingRequest);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"propose firing_cells {firingReply["failure"].ToJson()}: {e.Message}", e);
            }

            var n = observed!["proposed"].AsSlice().Count;
            if (n != Geometry.MaxCells)
            {
                throw new AcceptanceException($"propose firing_cells at radius {CaseSupport.Tag(Geometry.MaxRadius)} gave {CaseSupport.Tag(n)} cells, want {CaseSupport.Tag(Geometry.MaxCells)}");
            }

            samples.Add(observed["mainThreadMs"].AsNumberOrZero());
        }

        samples.Sort();
        var worst = samples[^1];

        // Scan and scoring are linear in targets and hostiles: 8 each here, the caps are 16.
        var projected = worst * Geometry.MaxHostiles / pawns.Count;
        report["proposeTiming"] = new JsonObject
        {
            ["targets"] = targets.Count,
            ["hostiles"] = pawns.Count,
            ["radius"] = Geometry.MaxRadius,
            ["msSorted"] = new JsonArray([.. samples.Select(m => (JsonNode?)JsonValue.Create(m))]),
            ["worstMs"] = worst,
            ["projectedAtCapsMs"] = projected,
        };
        if (projected > GeometryBudgetMs)
        {
            throw new AcceptanceException($"propose at the caps projects {projected.ToString("0.0", CultureInfo.InvariantCulture)} ms (worst {worst.ToString("0.0", CultureInfo.InvariantCulture)} ms), over {GeometryBudgetMs.ToString("0", CultureInfo.InvariantCulture)} ms");
        }
    }

    // shots requires the fight's shots in the events section (the Verb_LaunchProjectile and Verb_MeleeAttack hooks).
    private static void Shots(Dictionary<string, int> kinds)
    {
        if (kinds.GetValueOrDefault("COMBAT_LOG_KIND_SHOT_FIRED") == 0)
        {
            throw new AcceptanceException($"a pawn went down but no shot_fired event was recorded: {KindsText(kinds)}");
        }
    }

    // fight runs the lab until a pawn goes down or dies, then checks that the event and the pawn's change came in one
    // frame at one watermark. Every frame carries the whole event ring, so only events not seen before are counted.
    private static async Task FightAsync(Harness h, CombatFrames f, Report report, CancellationToken cancellationToken)
    {
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var ticks = 0; ticks < FightTicks; ticks += StepTicks)
        {
            var (_, tick) = await CombatStage.TickAsync(h, StepTicks, cancellationToken).ConfigureAwait(false);
            var state = await f.NextAsync(tick, cancellationToken).ConfigureAwait(false);
            var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var row in state.Pawns)
            {
                rows[row["id"].AsString()] = row;
            }

            foreach (var e in state.Events)
            {
                if (!seen.Add(Geometry.EventId(e)))
                {
                    continue;
                }

                var kind = e["kind"].AsString();
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                switch (kind)
                {
                    case "COMBAT_LOG_KIND_DOWNED":
                        _ = rows.TryGetValue(e["thingId"].AsString(), out var downedRow);
                        report["eventKinds"] = KindsJson(kinds);
                        report["down"] = new JsonObject
                        {
                            ["event"] = e["at"]?.DeepClone(),
                            ["row"] = downedRow?.DeepClone(),
                            ["ticks"] = ticks + StepTicks,
                        };
                        if (downedRow is null)
                        {
                            // Downed then killed inside one step: its row is gone.
                            Shots(kinds);
                            return;
                        }

                        if (downedRow["downed"].AsBool() != true || !SameWatermark(downedRow["changed"].AsMap(), e["at"].AsMap()))
                        {
                            throw new AcceptanceException($"downing {e.ToJson()} without its pawn row at the same watermark: {downedRow.ToJson()}");
                        }

                        Shots(kinds);
                        return;
                    case "COMBAT_LOG_KIND_KILLED":
                        report["eventKinds"] = KindsJson(kinds);
                        report["down"] = new JsonObject { ["event"] = e["at"]?.DeepClone(), ["killed"] = e["thingId"].AsString(), ["ticks"] = ticks + StepTicks };
                        if (rows.ContainsKey(e["thingId"].AsString()))
                        {
                            throw new AcceptanceException($"death {e.ToJson()} with its pawn row still in the same frame");
                        }

                        Shots(kinds);
                        return;
                }
            }

            report["eventKinds"] = KindsJson(kinds);
        }

        if (kinds.GetValueOrDefault("COMBAT_LOG_KIND_SHOT_FIRED") == 0)
        {
            throw new AcceptanceException($"no shot fired in {CaseSupport.Tag(FightTicks)} ticks of lab-ranged");
        }

        throw new AcceptanceException($"nobody went down in {CaseSupport.Tag(FightTicks)} ticks of lab-ranged: {KindsText(kinds)}");
    }

    private static bool SameWatermark(JsonObject? a, JsonObject? b) =>
        Geometry.WatermarkTick(a) == Geometry.WatermarkTick(b) && Geometry.WatermarkSeq(a) == Geometry.WatermarkSeq(b);

    private static JsonObject KindsJson(Dictionary<string, int> kinds)
    {
        var o = new JsonObject();
        foreach (var (name, n) in kinds.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            o[name] = n;
        }

        return o;
    }

    private static string KindsText(Dictionary<string, int> kinds) =>
        "map[" + string.Join(" ", kinds.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{CaseSupport.Tag(pair.Value)}")) + "]";
}

/// <summary>The def rows' view of one weapon (the part of Go <c>DefinitionCatalog.WeaponOf</c> the mirror case reads).</summary>
internal static class WeaponCatalog
{
    private const string MeleeVerbClass = "RimWorld.Verb_MeleeAttack";

    /// <summary>
    /// Reads the definition catalog and reports whether the weapon def's first non-melee verb is a ranged one and its
    /// range. A melee verb is one whose class is, or derives from, <c>RimWorld.Verb_MeleeAttack</c> by the catalog's
    /// class chains.
    /// </summary>
    internal static async Task<(bool Ranged, double Range)> RangedFactsAsync(Harness h, JsonObject identity, string defName, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            "combat-definition-catalog",
            "observations_read_definition_catalog",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        var observed = reply["observed"].AsMap() ?? throw new AcceptanceException($"definition catalog not observed: {reply.ToJson()}");
        var row = Find(observed, defName) ?? throw new AcceptanceException($"contract: catalog has no def row for {defName}");
        var bases = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var chain in observed["classChains"].AsSlice())
        {
            var c = chain.AsMap() ?? [];
            bases[c["name"].AsString()] = [.. c["bases"].AsSlice().Select(b => b.AsString())];
        }

        foreach (var entry in row["verbs"].AsSlice())
        {
            var verb = entry.AsMap()?["value"].AsMap() ?? entry.AsMap() ?? [];
            var verbClass = verb["verbClass"].AsString();
            if (!bases.TryGetValue(verbClass, out var chainBases))
            {
                throw new AcceptanceException($"contract: catalog has no class chain for {verbClass}");
            }

            if (verbClass == MeleeVerbClass || chainBases.Contains(MeleeVerbClass, StringComparer.Ordinal))
            {
                continue;
            }

            return (true, verb["range"].AsNumberOrZero());
        }

        return (false, 0);
    }

    // The def row is the object carrying defName and a verbs list; the catalog groups rows by def message, so find it
    // by walking the document.
    private static JsonObject? Find(JsonNode? node, string defName)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["defName"].AsString() == defName && o["verbs"] is JsonArray)
                {
                    return o;
                }

                foreach (var (_, child) in o)
                {
                    if (Find(child, defName) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonArray a:
                foreach (var child in a)
                {
                    if (Find(child, defName) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            default:
                return null;
        }
    }
}
