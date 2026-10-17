using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>odyssey/lava-field</c> (Go <c>lava.go</c>): a colony starts beside a lava field and builds clear of it. The harm is
/// the game's own burn terrain, the colony's layout and field planning read it as a hazard, and the native postcondition
/// is that no field, pasture or building stands on it and no colonist stands in it (#1719).
/// </summary>
internal static class OdysseyLavaField
{
    // The ticks the field planner is given after the layout plan is recorded (the hourly layout trigger).
    private const ulong FieldHour = 2500;

    // The hazard patch is the 16x16 block, laid as one test/lab_stage terrain rectangle, whose south-west corner is PatchOffset cells east and
    // PatchHalf south of the lab's centre: beside the colonists' start, inside the plan's reach.
    private const int PatchSize = 16;
    private const int PatchOffset = 5;
    private const int PatchHalf = 8;

    internal static AcceptanceCase Case() => new()
    {
        Name = "odyssey/lava-field",
        Scope = "Issue #1719: on the blank lab with Odyssey, a 16x16 block of the game's own burn terrain (the catalog terrain def "
            + "whose burnDamage or heatPerTick is positive) lies beside the colonists; the served colony plans a layout and fields, and "
            + "natively every patch cell reads as a hazard in the map survey, no growing or pasture zone and no player building stands on "
            + "a hazard cell, and no colonist stands on one. A Go snapshot test cannot cover it: the hazard bit is a native read of the "
            + "terrain now at the cell (NativeOdysseyColony.IsHazard on the live TerrainGrid), and the zones and buildings are the game's "
            + "own state after the controller's writes; the planner's refusal of hazard cells replays offline in "
            + "policy TestZoningKeepsHazardOffCoreFieldPasture. No eruption is fired: no existing test op fires one.",
        Start = new FixtureStart { Op = CombatFixtures.StageTool, ArgsFrom = PatchArgsAsync, On = new LabStart() },

        // The Odyssey profile needs its own process.
        Expansions = ["ludeon.rimworld.odyssey"],
        NoKeep = true,
        NoCheckpoint = true,
        Keep = ["Food"],
        Serve = new ServeSpec
        {
            Families = ["shelter", "expansion", "field"],
            NativeTimeout = TimeSpan.FromSeconds(30),
            Prefix = "odyssey-lava-field",
        },
        Budget = TimeSpan.FromMinutes(8),
        Crew = new Crew { Size = 3 },
        Reason = "one serve until the layout plan is recorded and the field planner has had an in-game hour",
        Run = RunAsync,
    };

    // The hazard patch's cells.
    private static List<Cell> PatchCells()
    {
        var centre = LabWorld.LabMapSize / 2;
        var cells = new List<Cell>();
        for (var z = centre - PatchHalf; z < centre - PatchHalf + PatchSize; z++)
        {
            for (var x = centre + PatchOffset; x < centre + PatchOffset + PatchSize; x++)
            {
                cells.Add(new Cell(x, z));
            }
        }

        return cells;
    }

    // The terrain defs the map survey reads as hazards: the top terrain's own def flags (Go bridge SurveyFromCells).
    private static bool IsHazard(JsonObject row) =>
        row["dangerous"].AsBool() == true || row["burnDamage"].AsNumber() > 0 || row["heatPerTick"].AsNumber() > 0 || row["toxicBuildupFactor"].AsNumber() > 0;

    private static async Task<IReadOnlyList<JsonObject>> TerrainRowsAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, "observations_read_definition_catalog", new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } }, cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        return [.. observed["terrainDefs"].AsSlice().Select(r => r.AsMap() ?? [])];
    }

    // The fixture op's arguments: the patch's cells and the burn terrain def, read from the definition catalog by the
    // def's own flags.
    private static async Task<JsonObject?> PatchArgsAsync(Harness h, CancellationToken cancellationToken)
    {
        var identity = await Session.ReadIdentityAsync(h, "lava-identity", cancellationToken).ConfigureAwait(false);
        var burning = (await TerrainRowsAsync(h, identity, "lava-catalog", cancellationToken).ConfigureAwait(false))
            .Where(row => row["burnDamage"].AsNumber() > 0 || row["heatPerTick"].AsNumber() > 0)
            .Select(row => row["defName"].AsString())
            .Order(StringComparer.Ordinal)
            .ToList();
        if (burning.Count == 0)
        {
            throw new AcceptanceException("the definition catalog holds no terrain with burnDamage or heatPerTick above zero (Odyssey inactive?)");
        }

        var centre = LabWorld.LabMapSize / 2;
        var spec = new JsonObject
        {
            ["terrain"] = new JsonObject
            {
                ["def"] = burning[0],
                ["minX"] = centre + PatchOffset,
                ["minZ"] = centre - PatchHalf,
                ["maxX"] = centre + PatchOffset + PatchSize - 1,
                ["maxZ"] = centre - PatchHalf + PatchSize - 1,
            },
        };
        return new JsonObject { ["spec"] = spec.ToJsonString() };
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        if (prepared["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{CombatFixtures.StageTool} refused: {prepared.ToJson()}");
        }

        report["fixture"] = prepared.DeepClone();
        await ServeFieldsAsync(s, cancellationToken).ConfigureAwait(false);
        var h = s.Reattach();
        _ = await h.CallAsync("pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);

        // The map survey: the whole map's cells with the hazard terrain flagged from the catalog, and the player's
        // edifices read off the cells' thing lists.
        var identity = s.Identity;
        var tick = await ReadTickAsync(h, identity, cancellationToken).ConfigureAwait(false);
        var hazardDefs = (await TerrainRowsAsync(h, identity, "lava-catalog-after", cancellationToken).ConfigureAwait(false))
            .Where(IsHazard)
            .Select(row => row["defName"].AsString())
            .ToHashSet(StringComparer.Ordinal);
        var (grid, built) = await ReadMapAsync(h, identity, cancellationToken).ConfigureAwait(false);
        var hazard = grid.Cells().Where(c => c.Terrain is { } terrain && hazardDefs.Contains(terrain)).Select(c => c.Cell).ToHashSet();
        var unread = PatchCells().Where(c => !hazard.Contains(c)).ToList();
        if (unread.Count > 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{unread.Count} of {PatchSize * PatchSize} laid patch cells do not read as hazard in the map survey, first {unread[0]}"));
        }

        var builtOnHazard = hazard.Where(built.Contains).ToList();
        var zoned = 0;
        var onHazard = new List<Cell>();
        foreach (var cells in grid.ZoneCells().Values)
        {
            zoned += cells.Count;
            onHazard.AddRange(cells.Where(hazard.Contains));
        }

        var standing = await ColonistsOnHazardAsync(h, identity, hazard, cancellationToken).ConfigureAwait(false);
        report["lava"] = new JsonObject
        {
            ["hazard_cells"] = hazard.Count,
            ["zone_cells"] = zoned,
            ["zone_cells_on_hazard"] = onHazard.Count,
            ["built_on_hazard"] = builtOnHazard.Count,
            ["colonists_on_hazard"] = new JsonArray([.. standing.Select(p => (JsonNode?)JsonValue.Create(p))]),
            ["tick"] = tick,
        };
        if (onHazard.Count > 0)
        {
            throw new AcceptanceException($"{onHazard.Count} zone cells lie on hazard terrain, first {onHazard[0]}");
        }

        if (builtOnHazard.Count > 0)
        {
            throw new AcceptanceException($"{builtOnHazard.Count} player buildings stand on hazard terrain, first {builtOnHazard[0]}");
        }

        if (standing.Count > 0)
        {
            throw new AcceptanceException($"colonists stand on hazard terrain: [{string.Join(' ', standing)}]");
        }

        if (zoned == 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the colony laid no zone by tick {tick}: the hazard check proved nothing"));
        }
    }

    private static async Task<ulong> ReadTickAsync(Harness h, JsonObject identity, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync("lava-tick", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (_, loaded) = Harness.Outcome(reply, "loaded");
        _ = identity;
        return loaded["context"].Member("tick").AsUInt64() ?? 0;
    }

    // The whole map's cells (an anchor read for the map's size, then the map) with the cells a player edifice covers.
    private static async Task<(MapCellGrid Grid, HashSet<Cell> Built)> ReadMapAsync(Harness h, JsonObject identity, CancellationToken cancellationToken)
    {
        async Task<(Observations.CellsSnapshot Snapshot, MapCellGrid Grid)> Read(string label, long maxX, long maxZ)
        {
            var reply = await h.WireAsync(
                label,
                "observations_get_cells",
                new JsonObject
                {
                    ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                    ["rectangle"] = new JsonObject
                    {
                        ["minimum"] = new JsonObject { ["x"] = 0, ["z"] = 0 },
                        ["maximum"] = new JsonObject { ["x"] = maxX, ["z"] = maxZ },
                    },
                },
                cancellationToken).ConfigureAwait(false);
            var (_, observed) = Harness.Outcome(reply, "observed");
            return CellReads.DecodeCells(observed);
        }

        var (anchor, _) = await Read("lava-cells-size", 0, 0).ConfigureAwait(false);
        var width = (long)(anchor.MapSize?.Width ?? 0);
        var height = (long)(anchor.MapSize?.Height ?? 0);
        var (_, grid) = await Read("lava-cells", width - 1, height - 1).ConfigureAwait(false);
        var built = new HashSet<Cell>();
        var cells = grid.Cells();
        var things = grid.Things();
        for (var i = 0; i < cells.Count; i++)
        {
            if (things[i].Any(t => t.Has(P.ThingMarks.Edifice) && t.Faction == P.ThingFaction.Player))
            {
                _ = built.Add(cells[i].Cell);
            }
        }

        return (grid, built);
    }

    // Serves the controller until the brain has reviewed and the field planner has had an in-game hour, then stops it. The
    // Go wait read the journal for the layout plan record; the player API shows the review ticks, so the hour counts from
    // the first review the service completes.
    private static async Task ServeFieldsAsync(ISession s, CancellationToken cancellationToken)
    {
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            ulong? first = null;
            try
            {
                _ = await service.WaitReviewAsync(
                    new Wait { Stall = Wait.StallBudget(), Ceiling = TimeSpan.FromMinutes(5) },
                    routines =>
                    {
                        if (routines["lastReviewTick"].AsUInt64() is not { } tick)
                        {
                            return false;
                        }

                        first ??= tick;
                        if (tick < first + FieldHour)
                        {
                            return false;
                        }

                        s.Report["plan"] = new JsonObject { ["tick"] = first, ["review_tick"] = tick };
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (WaitError e)
            {
                throw new AcceptanceException($"wait for a layout plan and its first field hour: {e.Message}", e);
            }
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }
    }

    // The colonists whose cell is a hazard cell.
    private static async Task<List<string>> ColonistsOnHazardAsync(Harness h, JsonObject identity, HashSet<Cell> hazard, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            "lava-colonists",
            "observations_list_pawns",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["filter"] = new JsonObject { ["colonist"] = true },
            },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var rows = observed["pawns"].AsSlice();
        if (rows.Count == 0)
        {
            throw new AcceptanceException($"no colonist rows to check against the hazard terrain: {observed.ToJson()}");
        }

        var standing = new List<string>();
        foreach (var raw in rows)
        {
            var pawn = raw.AsMap()?["pawn"].AsMap();
            var at = pawn?["position"].AsMap();
            var cell = new Cell((int)at.Member("x").AsNumber(), (int)at.Member("z").AsNumber());
            if (hazard.Contains(cell))
            {
                standing.Add(string.Create(CultureInfo.InvariantCulture, $"{pawn.Member("id").AsString()}@{cell.X},{cell.Z}"));
            }
        }

        return standing;
    }
}
