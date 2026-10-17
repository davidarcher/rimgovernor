using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Journal = RimGovernor.Brain.Store.Store;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using ReviewRecord = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The shell helpers <c>shelter/bunks-first</c> runs on (Go <c>shelter/shell.go</c>): the durable shell plan's geometry
/// and lineage, the service starts, the furnished bed and the native room checks. The staged-ring shelter cases are
/// snapshot tests (<c>buildingruntime.TestShelterSitingSnapshots</c>). The work family is left out of
/// <see cref="Families"/> because its planner refuses the tribal8 baseline's work priorities and one failing planner
/// cancels the whole step, and the acquisition family because its food planner times out under load.
/// </summary>
internal static class ShelterShell
{
    internal static readonly string[] Families = ["shelter", "sleeping"];

    /// <summary>The wall-clock ceiling for each construction phase.</summary>
    internal static readonly TimeSpan BuildWait = TimeSpan.FromMinutes(30);

    /// <summary>The wall-clock ceiling for a bed completed inside the finished hut.</summary>
    internal static readonly TimeSpan FurnishWait = TimeSpan.FromMinutes(15);

    /// <summary>The game-time budget for native construction to finish once the plan has placed its blueprints.</summary>
    internal const ulong BuiltTicks = 60000;

    /// <summary>The game-time budget for the game to roof the closed ring (#3018).</summary>
    private const ulong RoofBudget = 10000;

    /// <summary>The shelter's planned ring as the recorded layout plan holds it.</summary>
    internal sealed class Ring
    {
        internal required RoomFootprint Footprint { get; init; }

        /// <summary>The ring: every wall cell and the door.</summary>
        internal required HashSet<Cell> Cells { get; init; }

        /// <summary>The method glob of the ring's build waves ("shelter-shell-room&lt;id&gt;-build-*", plannedRoomMethod).</summary>
        internal required string Pattern { get; init; }

        /// <summary>The shell's geometry for the report.</summary>
        internal JsonObject Describe()
        {
            var bounds = Footprint.Bounds;
            return new JsonObject
            {
                ["pattern"] = Pattern,
                ["door"] = CaseSupport.Cell(Footprint.Door.X, Footprint.Door.Z),
                ["entrance"] = Footprint.Entrance.ToString().ToLowerInvariant(),
                ["interior_cells"] = Footprint.Interior.Count,
                ["wall_cells"] = Footprint.Walls.Count,
                ["bounds"] = new JsonObject { ["x"] = bounds.X, ["z"] = bounds.Z, ["width"] = bounds.Width, ["height"] = bounds.Height },
            };
        }

        internal string BoundsText()
        {
            var bounds = Footprint.Bounds;
            return string.Create(CultureInfo.InvariantCulture, $"{{X:{bounds.X} Z:{bounds.Z} Width:{bounds.Width} Height:{bounds.Height}}}");
        }
    }

    /// <summary>
    /// The ring's build waves the controller has issued. A world interruption or a restart cancels a live wave and the
    /// next review issues a successor for the cells still missing, so the ring's history is a chain of waves; the union
    /// of their completed cells is the ring placed.
    /// </summary>
    internal sealed record Lineage(int Plans, int Ordered, HashSet<Cell> Completed)
    {
        /// <summary>
        /// Counts the ring cells neither a wave completed nor a wall standing before the run held: the planner matches a
        /// standing wall as it is and orders no wave for that cell, so a baseline wall on the ring is placed already.
        /// </summary>
        internal int Missing(HashSet<Cell> ring, HashSet<Cell> standing) => ring.Count(c => !Completed.Contains(c) && !standing.Contains(c));
    }

    /// <summary>The wait under a ceiling and the shared stall budget, also ended by the service exiting on its own.</summary>
    internal static Wait WaitOf(TimeSpan ceiling, ServiceProcess service) => new() { Ceiling = ceiling, Stall = Wait.StallBudget(), Terminal = service.Exited };

    /// <summary>
    /// Launches the service (<paramref name="previous"/> null) or restarts it on the same durable state, acquires player
    /// authority and keeps it granted until stopped.
    /// </summary>
    internal static async Task<ServiceProcess> StartAsync(ISession s, ServiceProcess? previous, CancellationToken cancellationToken)
    {
        var proc = previous is null
            ? await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false)
            : await previous.RestartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await proc.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = await proc.StopAsync().ConfigureAwait(false);
            throw;
        }

        proc.KeepAuthority(cancellationToken);
        return proc;
    }

    /// <summary>
    /// Reads the ring's waves from the plan history, retired ones included. A wave ordering anything but a wall or door on
    /// the ring is a second shell and fails the run.
    /// </summary>
    internal static Lineage ReadLineage(Journal st, Ring sh, CancellationToken cancellationToken)
    {
        var plans = st.PlanHistoryWithMethods(256, [sh.Pattern], cancellationToken);
        var completed = new HashSet<Cell>();
        var ordered = 0;
        foreach (var plan in plans)
        {
            for (var i = 0; i < plan.Spec.Actions.Count; i++)
            {
                var b = plan.Spec.Actions[i].Building;
                if (b is null || (b.Definition != "Wall" && b.Definition != "Door"))
                {
                    throw new AcceptanceException($"ring wave {plan.Spec.Id.Value} holds a non-ring action");
                }

                if (!sh.Cells.Contains(b.Cell))
                {
                    throw new AcceptanceException($"ring wave {plan.Spec.Id.Value} orders {ShelterStage.Describe(b.Cell)} off the planned ring (a second shell)");
                }

                var v = plan.Progress[i].View;
                if (v.Stage == Stage.Completed)
                {
                    completed.Add(b.Cell);
                }

                if (v.Attempt.Value > 0)
                {
                    ordered++;
                }
            }
        }

        return new Lineage(plans.Count, ordered, completed);
    }

    /// <summary>Polls the ring's waves until <paramref name="done"/>; the progress signature is the wave count and the cells placed.</summary>
    internal static async Task WaitLineageAsync(Journal st, Ring sh, Wait w, Func<Lineage, bool> done, CancellationToken cancellationToken)
    {
        Lineage last = new(0, 0, []);
        try
        {
            await w.WaitProgressAsync(
                ct =>
                {
                    var l = ReadLineage(st, sh, ct);
                    last = l;
                    return Task.FromResult(done(l) ? ("", true) : (Wait.Signature(l.Plans, l.Ordered, l.Completed.Count), false));
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException(
                string.Create(CultureInfo.InvariantCulture, $"ring waves={last.Plans} ordered={last.Ordered} placed={last.Completed.Count} of {sh.Cells.Count}: {e.Message}"),
                e);
        }
    }

    /// <summary>Polls the store until the recorded layout plan holds the shelter and reads its ring: the interior and door the planner raises the walls on.</summary>
    internal static async Task<Ring> WaitShellAsync(Journal st, Wait w, CancellationToken cancellationToken)
    {
        Ring? found = null;
        try
        {
            await w.WaitProgressAsync(
                async ct =>
                {
                    ReviewRecord review;
                    try
                    {
                        review = await st.LoadRoundsAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        return (Wait.Signature("no-review", e.Message), false);
                    }

                    Store.LayoutPlanRecord? record;
                    try
                    {
                        record = st.LayoutPlan(review.Snapshot, review.Tick, ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        return (Wait.Signature("no-plan", e.Message), false);
                    }

                    if (record?.Plan is not { } plan)
                    {
                        return (Wait.Signature("no-plan"), false);
                    }

                    var rooms = ShelterStage.RoomsOf(plan, PolicyFunctions.PlannedShelter);
                    if (rooms.Count == 0)
                    {
                        return (Wait.Signature("no-shelter", record.Tick.Value), false);
                    }

                    var room = rooms[0];
                    RoomFootprint footprint;
                    try
                    {
                        footprint = room.Footprint();
                    }
                    catch (DomainException e)
                    {
                        throw new AcceptanceException($"the planned shelter {ShelterStage.Describe(room.Region.Bounds())} is no room footprint: {e.Message}", e);
                    }

                    var cells = new HashSet<Cell> { footprint.Door };
                    foreach (var c in footprint.Walls)
                    {
                        cells.Add(c);
                    }

                    var inner = room.Region.Bounds();
                    found = new Ring
                    {
                        Footprint = footprint,
                        Cells = cells,
                        Pattern = string.Create(CultureInfo.InvariantCulture, $"{Wire.Name(room.Role)}-shell-room{room.Id}-build-*"),
                    };
                    return ("", true);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("no layout plan holds a shelter: " + e.Message, e);
        }

        return found!;
    }

    /// <summary>
    /// Has <c>test/hut_shell_fixture</c> unforbid the starting supplies a fresh load drops forbidden, so the shell's WoodLog
    /// stock admits its plan without the supply family: that family cost one worker dispatch (3-4s under peer load) per
    /// stack, fifteen of them in run 2.
    /// </summary>
    internal static async Task AllowSuppliesAsync(Harness h, string label, Report report, CancellationToken cancellationToken)
    {
        var result = await h.CallAsync(label, "test/hut_shell_fixture", new JsonObject { ["action"] = "allow" }, cancellationToken).ConfigureAwait(false);
        if (result["success"].AsBool() != true)
        {
            throw new AcceptanceException($"hut_shell_fixture allow refused: {result.ToJson()}");
        }

        report[label.Replace('-', '_')] = result["allowed"]?.DeepClone();
    }

    /// <summary>The held cells of a whole-map read with the things standing on each.</summary>
    internal sealed class ThingGrid
    {
        internal required MapCellGrid Grid { get; init; }

        internal required IReadOnlyList<GridCell> Cells { get; init; }

        /// <summary>The things on each held cell, aligned with <see cref="Cells"/>.</summary>
        internal required IReadOnlyList<IReadOnlyList<Thing>> Things { get; init; }

        /// <summary>Reads the whole map's cells with their things (<c>observations_get_cells</c>: an anchor read for the size, then the map).</summary>
        internal static async Task<ThingGrid> ReadAsync(Harness h, string label, JsonObject identity, CancellationToken cancellationToken)
        {
            async Task<JsonObject> Read(string suffix, long maxX, long maxZ)
            {
                var reply = await h.WireAsync(
                    label + suffix,
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
                return Harness.Outcome(reply, "observed").Value;
            }

            var (anchor, _) = CellReads.DecodeCells(await Read("-size", 0, 0).ConfigureAwait(false));
            var width = (long)(anchor.MapSize?.Width ?? 0);
            var height = (long)(anchor.MapSize?.Height ?? 0);
            var (_, grid) = CellReads.DecodeCells(await Read(string.Empty, width - 1, height - 1).ConfigureAwait(false));
            return new ThingGrid { Grid = grid, Cells = grid.Cells(), Things = grid.Things() };
        }
    }

    /// <summary>
    /// Verifies the finished hut natively: it is one proper roofed indoor room of the planned interior behind a native
    /// doorway, its beds stand inside off the doorway aisle, and every player wall or door near it stands built on the ring.
    /// </summary>
    internal static async Task VerifyNativeAsync(
        Harness h,
        JsonObject expected,
        Ring sh,
        HashSet<Cell> baseline,
        IReadOnlyList<Cell> bedCells,
        Report report,
        CancellationToken cancellationToken)
    {
        _ = await h.CallAsync("pause-for-verify", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var identity = await Session.ReadIdentityAsync(h, "identity-after", cancellationToken).ConfigureAwait(false);
        if (!ServiceProcess.MatchesIdentity(identity, expected))
        {
            throw new AcceptanceException($"identity changed during the run: {identity.ToJson()}");
        }

        JsonObject Scope() => new() { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } };

        var inside = new HashSet<Cell>(sh.Footprint.Interior);
        var room = HutRoomCells(sh.Footprint.Interior, baseline);
        var door = sh.Footprint.Door;
        JsonObject? hut = null;
        JsonObject? doorway = null;
        var near = string.Empty;

        // Reads the native rooms; false when none holds exactly the planned interior (the room is not roofed yet, or a near miss).
        async Task<bool> ReadHutAsync(string label, CancellationToken ct)
        {
            var reply = await h.WireAsync(label, "observations_list_rooms", Scope(), ct).ConfigureAwait(false);
            var (_, observed) = Harness.Outcome(reply, "observed");
            var grid = await ThingGrid.ReadAsync(h, label + "-cells", identity, ct).ConfigureAwait(false);
            var gridRooms = grid.Grid.RoomCells();
            hut = null;
            doorway = null;
            near = string.Empty;
            foreach (var raw in observed["rooms"].AsSlice())
            {
                var row = raw.AsMap() ?? [];
                var cells = (row["gridRoom"] is { } key ? gridRooms.GetValueOrDefault((int)key.AsNumber()) : null) ?? [];
                if (cells.Count == 1 && cells[0] == door)
                {
                    doorway = row;
                    continue;
                }

                if (cells.Count != room.Count)
                {
                    if (cells.Count > 1 && cells.Count >= room.Count - 4 && cells.Count < room.Count)
                    {
                        near = DescribeNear(grid, cells, room, row);
                    }

                    continue;
                }

                if (cells.All(room.Contains))
                {
                    hut = row;
                }
            }

            return hut is not null;
        }

        // Roofing is the game's own work after the ring closes: builders roof an enclosed room over the following hours,
        // and until the last cell is roofed the room is not a native room of the planned interior at all (an unroofed
        // cell joins the outdoors). The shell can complete just before this read, so run the game, re-reading the room,
        // for the roofing budget (#3018).
        ulong waited;
        try
        {
            waited = await RunAt.RunUntilAsync(
                h,
                "shell-roofed",
                RoofBudget,
                new Wait { Ceiling = BuildWait, Stall = Wait.StallBudget() },
                async ct =>
                {
                    if (!await ReadHutAsync("rooms-after", ct).ConfigureAwait(false))
                    {
                        return ("no-hut", false);
                    }

                    var open = hut!["openRoofCount"].AsNumber();
                    return (string.Create(CultureInfo.InvariantCulture, $"open={open}"), open == 0);
                },
                HarnessQuiet.Get(h),
                report,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"shell-roofed: {e.Message} (near miss: {near})", e);
        }

        report["roof_wait_ticks"] = waited;

        // Doorway rooms are excluded from the typed census unless outdoors rooms are included; a second census without
        // cells (the outdoors mega-room would otherwise list the whole map) finds the door's one-tile room by its extents.
        if (doorway is null)
        {
            var scope = Scope();
            scope["includeOutdoors"] = true;
            var reply = await h.WireAsync("doorways-after", "observations_list_rooms", scope, cancellationToken).ConfigureAwait(false);
            var (_, all) = Harness.Outcome(reply, "observed");
            foreach (var raw in all["rooms"].AsSlice())
            {
                var row = raw.AsMap() ?? [];
                var extents = row["extents"].AsMap();
                var lo = extents?["minimum"].AsMap();
                var hi = extents?["maximum"].AsMap();
                if (row["doorway"].AsBool() == true && lo is not null && hi is not null && At(lo) == door && At(hi) == door)
                {
                    doorway = row;
                }
            }
        }

        var summary = new JsonObject
        {
            ["id"] = hut!["id"]?.DeepClone(),
            ["role"] = hut["role"]?.DeepClone(),
            ["properRoom"] = hut["properRoom"]?.DeepClone(),
            ["outdoors"] = hut["outdoors"]?.DeepClone(),
            ["openRoofCount"] = hut["openRoofCount"]?.DeepClone(),
            ["cellCount"] = hut["cellCount"]?.DeepClone(),
            ["beds"] = hut["beds"].AsSlice().Count,
            ["pawns"] = hut["pawns"].AsSlice().Count,
        };
        report["native_hut"] = summary;
        if (hut["properRoom"].AsBool() != true || hut["outdoors"].AsBool() == true)
        {
            throw new AcceptanceException($"hut is not a proper indoor room: {summary.ToJson()}");
        }

        if (hut["openRoofCount"].AsNumber() != 0)
        {
            throw new AcceptanceException($"hut has {hut["openRoofCount"].ToText()} unroofed cells");
        }

        if (doorway is null || doorway["doorway"].AsBool() != true)
        {
            throw new AcceptanceException($"door cell {ShelterStage.Describe(door)} is not a native doorway room");
        }

        report["native_doorway"] = new JsonObject { ["id"] = doorway["id"]?.DeepClone(), ["doorway"] = doorway["doorway"]?.DeepClone() };

        // Aisle: the interior cell straight inside the door must stay free of beds, and every native bed must lie inside the hut.
        var aisle = new HashSet<Cell>(PolicyFunctions.DoorwayAisles(new Bounds(1 << 30, 1 << 30), [new SiteCell { Cell = door, Doorway = Fact.Known(true) }]));
        var beds = hut["beds"].AsSlice();
        if (beds.Count == 0)
        {
            throw new AcceptanceException("native hut lists no beds");
        }

        var rows = await h.BuildingRowsAsync("beds", expected, cancellationToken).ConfigureAwait(false);
        var bedReport = new JsonArray();
        foreach (var raw in beds)
        {
            var reference = raw.AsMap() ?? [];
            if (!rows.TryGetValue(reference["id"].AsString(), out var bed))
            {
                throw new AcceptanceException($"hut bed {reference["id"].ToText()} is not in the building table");
            }

            var occupied = new List<Cell>();
            foreach (var c in CellReads.RectCells(bed["occupied"]))
            {
                occupied.Add(c);
                if (!inside.Contains(c))
                {
                    throw new AcceptanceException($"bed cell {ShelterStage.Describe(c)} lies outside the hut");
                }

                if (aisle.Contains(c))
                {
                    throw new AcceptanceException($"bed cell {ShelterStage.Describe(c)} blocks the doorway aisle");
                }
            }

            var building = bed["building"].AsMap() ?? [];
            bedReport.Add(new JsonObject { ["id"] = building["id"]?.DeepClone(), ["cells"] = ShelterStage.CellsJson(occupied), ["status"] = bed["status"]?.DeepClone() });
        }

        report["native_beds"] = bedReport;
        report["planned_bed_cells"] = ShelterStage.CellsJson(bedCells);

        // No second shell: every player wall or door anywhere near this hut stands on its ring, one per cell, and none is
        // still a blueprint or frame.
        var b = sh.Footprint.Bounds;
        var listing = Scope();
        listing["statuses"] = new JsonArray("all");
        listing["playerOnly"] = true;
        var walls = await h.WireAsync("walls-after", "observations_list_buildings", listing, cancellationToken).ConfigureAwait(false);
        JsonObject listed;
        try
        {
            (_, listed) = Harness.Outcome(walls, "observed");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"walls-after: {e.Message}", e);
        }

        var centerX = b.X + (b.Width / 2);
        var centerZ = b.Z + (b.Height / 2);
        var ring = new HashSet<Cell>(sh.Footprint.Walls);
        var standing = new Dictionary<Cell, string>();
        foreach (var raw in listed["buildings"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var def = row["buildDefName"].AsString();
            if (def.Length == 0)
            {
                def = row["building"].AsMap()?["defName"].AsString() ?? "";
            }

            if (def != "Wall" && def != "Door")
            {
                continue;
            }

            var building = row["building"].AsMap() ?? [];
            var pos = building["position"].AsMap() ?? [];
            var c = At(pos);
            var dx = c.X - centerX;
            var dz = c.Z - centerZ;
            if ((dx * dx) + (dz * dz) > 64 * 64)
            {
                continue;
            }

            if (!ring.Contains(c) && baseline.Contains(c))
            {
                continue; // a baseline wall on the interior: not ordered by this run
            }

            if (!ring.Contains(c))
            {
                throw new AcceptanceException($"player {def} at {ShelterStage.Describe(c)} stands off the hut ring: a second shell was ordered");
            }

            if (row["status"].AsString() != "BUILDING_STATUS_BUILT")
            {
                throw new AcceptanceException($"{def} at {ShelterStage.Describe(c)} is still {row["status"].ToText()}");
            }

            if (!standing.TryAdd(c, def))
            {
                throw new AcceptanceException($"two structures at {ShelterStage.Describe(c)}");
            }
        }

        if (standing.Count != ring.Count)
        {
            throw new AcceptanceException($"{standing.Count} walls and doors stand on a ring of {ring.Count} cells");
        }

        report["native_ring"] = new JsonObject { ["cells"] = ring.Count, ["built"] = standing.Count };
    }

    private static Cell At(JsonObject m) => new((int)m["x"].AsNumber(), (int)m["z"].AsNumber());

    /// <summary>
    /// Ends what a killed service leaves running: its clock epoch's lease (up to 30 s) and authority grant outlive the
    /// process, and when the lease lapses native stops the clock and pauses the game with no letter, which is the pause
    /// <see cref="WaitBuiltAsync"/> otherwise met mid-wait. The harness pauses the game, revokes the grant and then runs the
    /// game itself.
    /// </summary>
    internal static async Task ReleaseServiceAsync(Harness h, JsonObject identity, Report report, CancellationToken cancellationToken)
    {
        _ = await h.CallAsync("service-released-pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        var revoked = await Authority.ReleaseAuthorityAsync(h, identity, cancellationToken).ConfigureAwait(false);
        if (revoked is not null)
        {
            report["authority_released"] = revoked;
        }
    }

    /// <summary>
    /// Runs the game until a built (not blueprint or frame) Wall or Door stands on every shell cell and a built Bed covers
    /// every bed cell. An intent-mode BuildingAction's Completed stage means its blueprint was placed, so construction is
    /// proven natively.
    /// </summary>
    internal static async Task WaitBuiltAsync(Harness h, JsonObject expected, Ring sh, IReadOnlyList<Cell> bedCells, Report report, CancellationToken cancellationToken)
    {
        ulong ticks;
        try
        {
            ticks = await RunAt.RunUntilAsync(
                h,
                "shell-built",
                BuiltTicks,
                new Wait { Ceiling = BuildWait, Stall = Wait.StallBudget() },
                async ct =>
                {
                    var reply = await h.WireAsync(
                        "shell-built",
                        "observations_list_buildings",
                        new JsonObject
                        {
                            ["scope"] = new JsonObject { ["expectedIdentity"] = expected.DeepClone() },
                            ["defNames"] = new JsonArray("Wall", "Door", "Bed"),
                            ["statuses"] = new JsonArray("built"),
                            ["playerOnly"] = true,
                        },
                        ct).ConfigureAwait(false);
                    var (_, observed) = Harness.Outcome(reply, "observed");
                    var built = new Dictionary<Cell, string>();
                    foreach (var raw in observed["buildings"].AsSlice())
                    {
                        var row = raw.AsMap() ?? [];
                        var def = row["building"].AsMap()?["defName"].AsString() ?? "";
                        foreach (var cell in CellReads.RectCells(row["occupied"]))
                        {
                            built[cell] = def;
                        }
                    }

                    var missing = 0;
                    foreach (var c in sh.Cells)
                    {
                        var d = built.GetValueOrDefault(c, "");
                        if (d != "Wall" && d != "Door")
                        {
                            missing++;
                        }
                    }

                    foreach (var c in bedCells)
                    {
                        if (built.GetValueOrDefault(c, "") != "Bed")
                        {
                            missing++;
                        }
                    }

                    return (string.Create(CultureInfo.InvariantCulture, $"missing={missing}"), missing == 0);
                },
                HarnessQuiet.Get(h),
                report,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException)
        {
            report["built_wait_ticks"] = 0;
            throw;
        }

        report["built_wait_ticks"] = ticks;
    }

    /// <summary>
    /// For a native room one short of the planned interior, names the interior cells it lacks and what stands on each, so a
    /// near miss says which cell is no room cell and why.
    /// </summary>
    private static string DescribeNear(ThingGrid grid, IReadOnlyList<Cell> cells, HashSet<Cell> inside, JsonObject row)
    {
        var held = new HashSet<Cell>(cells);
        var site = new Dictionary<Cell, int>();
        for (var i = 0; i < grid.Cells.Count; i++)
        {
            site[grid.Cells[i].Cell] = i;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"room {row["id"].ToText()} role {row["role"].ToText()} holds {cells.Count} of {inside.Count} interior cells; lacks");
        foreach (var c in inside.OrderBy(c => c.Z).ThenBy(c => c.X))
        {
            if (held.Contains(c))
            {
                continue;
            }

            if (!site.TryGetValue(c, out var at))
            {
                text.Append(CultureInfo.InvariantCulture, $" {ShelterStage.Describe(c)}{{room <none> walkable false roofed false indoors false terrain  things []}}");
                continue;
            }

            var cell = grid.Cells[at];
            var defs = string.Join(' ', grid.Things[at].Select(t => t.Def));
            text.Append(
                CultureInfo.InvariantCulture,
                $" {ShelterStage.Describe(c)}{{room {cell.Room?.ToString(CultureInfo.InvariantCulture) ?? "<none>"} walkable {Show(cell.Walkable)} roofed {Show(cell.Roofed)} indoors {Show(cell.Indoors)} terrain {cell.Terrain} things [{defs}]}}");
        }

        return text.ToString();
    }

    private static string Show(bool? value) => value == true ? "true" : "false";

    /// <summary>
    /// The cells the native room of the planned interior holds: the interior less the walls standing on it before the run.
    /// The planner sites the interior over such a wall; the reconcile takes it down only once the ring stands and only while
    /// the service runs, and the case stops the service when the ring and beds are placed, so the wall remains and is no
    /// room cell.
    /// </summary>
    internal static HashSet<Cell> HutRoomCells(IEnumerable<Cell> interior, HashSet<Cell> baseline)
    {
        var room = new HashSet<Cell>();
        foreach (var c in interior)
        {
            if (!baseline.Contains(c))
            {
                room.Add(c);
            }
        }

        return room;
    }
}
