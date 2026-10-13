using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/sea-ice-envelope</c> (#2892, Go <c>seaice_envelope.go</c>): a three-colonist base on a very cold ice sheet
/// is planned, built and lived in as one roofed envelope. The run is played in five-day segments, each a serve launch
/// that is stopped and reattached; at every boundary the reattached harness reads the colonists' health and the built
/// roofs and doors of the recorded plan. The run ends once the plan's roofed set stands, or fails at the ceiling.
/// <para>
/// Assertions on the plan the run ends with, against the built ground: (1) every non-outdoor planned room (an airlock
/// or porch child included) lies in LayoutPlan.Roofed and every cell of Roofed carries a built roof; (2) every airlock
/// child opens its parent's exterior door: the vestibule lies beyond that door, both doors and the two side walls
/// stand, and at least one airlock exists; (3) the door graph (the rooms' doors and LayoutPlan.Links) joins every
/// roofed room to every other without stepping on a cell outside the roofed set; (4) no colonist's Hypothermia passes
/// <see cref="PolicyFunctions.HypothermiaSerious"/> at any probe.
/// </para>
/// </summary>
internal static class LayoutSeaIceEnvelope
{
    /// <summary>One serve launch: five game days.</summary>
    private const ulong Segment = 5 * Wait.TicksPerDay;

    /// <summary>How long the base may take to roof: thirty game days.</summary>
    private const ulong Ceiling = 30 * Wait.TicksPerDay;

    /// <summary>The Ultrafast rate the solo cases measured on a debug colony; the budget derives from it.</summary>
    private const ulong TicksPerSecond = 899;

    /// <summary>The wall ceiling of one segment's watch.</summary>
    private static readonly TimeSpan Wall = TimeSpan.FromHours(2);

    /// <summary>The worst Hypothermia severity a probe may read (Go solo <c>hypothermiaCap</c>).</summary>
    private const double HypothermiaCap = PolicyFunctions.HypothermiaSerious;

    /// <summary>The ceiling's play time plus a launch, stop and probe per segment and the fixed start: about 58 minutes.</summary>
    private static readonly TimeSpan Budget =
        TimeSpan.FromSeconds(Ceiling / TicksPerSecond) + TimeSpan.FromMinutes(3 * (double)(Ceiling / Segment)) + TimeSpan.FromMinutes(7);

    internal static AcceptanceCase Case() => new()
    {
        Name = "layout/sea-ice-envelope",
        Scope = "Issue #2892 (epic #2855): on a three-colonist IceSheet start the built base is one roofed envelope. The plan's roofed set holds every " +
            "non-outdoor room and carries a built roof on every cell, each airlock child opens its parent's exterior door with both doors and walls standing, the " +
            "door graph joins the roofed rooms without stepping outside the roofed set, and no colonist's Hypothermia passes serious at a five-day probe. Nightly only.",
        Start = new FixtureStart
        {
            Op = DebugGame.QuietWorldTool,
            On = new ScenarioStart
            {
                Spec = new ScenarioSpec
                {
                    Scenario = DebugGame.DebugScenario,
                    Count = DebugGame.DebugColonists,
                    Seed = "layout-sea-ice-envelope-2892",
                    Difficulty = DebugGame.DebugDifficulty,
                    Biome = "IceSheet",
                    WorldTemperature = "VeryCold",
                    Size = new DebugStartSize { MapSize = 150, PlanetCoverage = 0.05 },
                    SaveName = "layout-sea-ice-envelope-2892",
                },
            },
        },
        RequiredOps = [PlanStage.Tool],
        Serve = new ServeSpec
        {
            NativeTimeout = TimeSpan.FromSeconds(30),
            StepStall = TimeSpan.FromSeconds(90),
            Prefix = "layout-envelope",
            PlayerSpeed = "Ultrafast",
        },
        Budget = Budget,
        Crew = new Crew { Size = DebugGame.DebugColonists },
        Reason = "a multi-room base on a very cold map takes game days to plan, build and roof; the fixture cannot stage a finished envelope, and " +
            "the probe at each five-day boundary is the cadence of the Hypothermia assertion",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var listed = await Colonists.ListColonistsAsync(s.Harness, "envelope-initial", false, false, cancellationToken).ConfigureAwait(false);
        var initial = new List<string>();
        foreach (var row in listed)
        {
            if (Colonists.RowID(row) is { Length: > 0 } id)
            {
                initial.Add(id);
            }
        }

        if (initial.Count != DebugGame.DebugColonists)
        {
            throw new AcceptanceException($"the start has {initial.Count} colonists, want {DebugGame.DebugColonists}: [{string.Join(' ', initial)}]");
        }

        report["initial_colonists"] = new JsonArray([.. initial.Select(id => (JsonNode?)id)]);

        var segments = new JsonArray();
        report["segments"] = segments;
        LayoutPlan? plan = null;
        var unmet = new List<string>();
        var elapsed = 0UL;
        for (var index = 0; elapsed < Ceiling; index++)
        {
            var label = string.Create(CultureInfo.InvariantCulture, $"seg{index:D2}");
            var row = new JsonObject { ["label"] = label, ["start_elapsed"] = elapsed };
            segments.Add(row);
            var played = await PlaySegmentAsync(s, label, row, initial, cancellationToken).ConfigureAwait(false);
            plan = played.Plan;
            unmet = played.Unmet;
            elapsed += played.Advanced;
            report["elapsed_ticks"] = elapsed;
            if (played.Error is not null)
            {
                throw new AcceptanceException(
                    string.Create(CultureInfo.InvariantCulture, $"{label} (day {elapsed / Wait.TicksPerDay} of {Ceiling / Wait.TicksPerDay}): {played.Error.Message}"),
                    played.Error);
            }

            if (unmet.Count == 0)
            {
                break;
            }

            if (played.Advanced == 0)
            {
                throw new AcceptanceException($"{label} advanced no ticks");
            }
        }

        if (unmet.Count > 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the envelope did not stand within {Ceiling / Wait.TicksPerDay} game days: {string.Join("; ", unmet)}"));
        }

        var standing = plan!;
        report["plan"] = standing.Summary();
        if (Join(UnroofedRooms(standing), AirlockCount(standing), DisconnectedRooms(standing)) is { } failed)
        {
            throw failed;
        }
    }

    /// <summary>What one segment's serve and probe came to (Go's return values of <c>playEnvelopeSegment</c>).</summary>
    private sealed record Played(LayoutPlan? Plan, ulong Advanced, List<string> Unmet, AcceptanceException? Error);

    /// <summary>
    /// Serves one window, then probes the reattached game. It returns the recorded plan, the ticks advanced and what of
    /// the built envelope is still missing (empty once it stands).
    /// </summary>
    private static async Task<Played> PlaySegmentAsync(ISession s, string label, JsonObject row, IReadOnlyList<string> initial, CancellationToken cancellationToken)
    {
        var spec = s.Spec with { Prefix = "layout-envelope-" + label };
        IWatchedService service;
        try
        {
            service = await ObservedSession.Of(s).ServeAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            return new Played(null, 0, [], new AcceptanceException("serve: " + e.Message, e));
        }

        var watch = new Report();
        AcceptanceException? watchError = null;
        try
        {
            await SustainedFood.WatchAsync(
                s.Config,
                service,
                new WatchConfig
                {
                    Watch = Wall,
                    Window = Segment,
                    Poll = TimeSpan.FromSeconds(10),

                    // A refusal is part of what the run records, never a reason to cut it.
                    FailFast = new FailFast { Disabled = true },
                },
                watch,
                new List<JsonObject>(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            watchError = e;
        }

        LayoutPlan? plan = null;
        AcceptanceException? readError = null;
        try
        {
            using var journal = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), cancellationToken).ConfigureAwait(false);
            var review = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
            var record = await journal.LayoutPlanAsync(review.Snapshot, review.Tick, cancellationToken).ConfigureAwait(false);
            plan = record?.Plan;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            readError = e as AcceptanceException ?? new AcceptanceException(e.Message, e);
        }

        row["keepalive"] = (await service.StopAsync().ConfigureAwait(false))?.DeepClone();
        var advanced = watch["window"].AsMap()?["elapsed_ticks"].AsUInt64() ?? 0UL;
        row["advanced"] = advanced;

        Harness h;
        try
        {
            h = s.Reattach();
        }
        catch (AcceptanceException e)
        {
            return new Played(plan, advanced, [], Join(watchError, readError, new AcceptanceException("reattach: " + e.Message, e)));
        }

        var failures = new List<Exception?> { watchError, readError };
        IReadOnlyList<JsonObject> pawns;
        try
        {
            pawns = await Colonists.ListColonistsAsync(h, label + "-health", true, true, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            failures.Add(e);
            return new Played(plan, advanced, [], Join([.. failures]));
        }

        failures.Add(AuditColonists(pawns, initial, row));
        var unmet = new List<string>();
        if (plan is null || plan.Roofed.Empty())
        {
            unmet.Add("the recorded plan holds no roofed set");
        }
        else
        {
            StageAudit read;
            try
            {
                read = await PlanStage.ReadAsync(h, label + "-envelope", [], EnvelopeCells(plan), cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                failures.Add(e);
                return new Played(plan, advanced, [], Join([.. failures]));
            }

            var unbuilt = UnbuiltRoofs(plan, read);
            var faults = AirlockFaults(plan, read);
            row["roofed_cells"] = plan.Roofed.Area();
            row["unroofed_cells"] = unbuilt.Count;
            row["airlock_faults"] = new JsonArray([.. faults.Select(f => (JsonNode?)f)]);
            if (unbuilt.Count > 0)
            {
                unmet.Add(string.Create(CultureInfo.InvariantCulture, $"{unbuilt.Count} of {plan.Roofed.Area()} roofed cells have no built roof (first {unbuilt[0]})"));
            }

            unmet.AddRange(faults);
        }

        return new Played(plan, advanced, unmet, Join([.. failures]));
    }

    /// <summary>The cells the native audit reads: the roofed set, and every airlock's door cells and side walls.</summary>
    private static List<StageCell> EnvelopeCells(LayoutPlan plan)
    {
        var seen = new HashSet<Cell>();
        var output = new List<StageCell>();

        void Add(Cell c)
        {
            if (seen.Add(c))
            {
                output.Add(new StageCell(c.X, c.Z));
            }
        }

        foreach (var c in plan.Roofed.Cells())
        {
            Add(c);
        }

        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedRole.Airlock)
            {
                continue;
            }

            foreach (var c in r.AirlockWalls())
            {
                Add(c);
            }

            Add(r.Door);
            Add(r.Vestibule());
            if (ParentOf(plan, r) is { } parent)
            {
                Add(parent.Door);
            }
        }

        return output;
    }

    /// <summary>The room <paramref name="child"/> is attached to.</summary>
    private static PlannedRoom? ParentOf(LayoutPlan plan, PlannedRoom child) =>
        child.Parent == 0 ? null : plan.Rooms.FirstOrDefault(r => r.Id == child.Parent);

    /// <summary>The cells of the plan's roofed set the audit reads without a roof.</summary>
    private static List<Cell> UnbuiltRoofs(LayoutPlan plan, StageAudit audit) =>
        [.. plan.Roofed.Cells().Where(c => !audit.CellAt(new StageCell(c.X, c.Z)).Roofed)];

    /// <summary>
    /// The ways a planned airlock fails to stand as its parent's exterior door opens it: no parent, a vestibule not
    /// beyond the parent's door, or a door, side wall or roof missing on the ground.
    /// </summary>
    private static List<string> AirlockFaults(LayoutPlan plan, StageAudit audit)
    {
        var output = new List<string>();
        CellRead At(Cell c) => audit.CellAt(new StageCell(c.X, c.Z));

        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedRole.Airlock)
            {
                continue;
            }

            var parent = ParentOf(plan, r);
            if (parent is null || parent.Role.IsOutdoor())
            {
                output.Add(string.Create(CultureInfo.InvariantCulture, $"airlock {r.Id} has no roofed parent room"));
                continue;
            }

            if (!Adjacent(parent.Door, r.Vestibule()))
            {
                output.Add(string.Create(CultureInfo.InvariantCulture, $"airlock {r.Id} vestibule {r.Vestibule()} is not beside its parent {parent.Id}'s door {parent.Door}"));
                continue;
            }

            foreach (var (name, c) in new[] { ("parent door", parent.Door), ("outer door", r.Door) })
            {
                if (!At(c).Edifice.Contains("door", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add(string.Create(CultureInfo.InvariantCulture, $"airlock {r.Id} {name} {c} holds \"{At(c).Edifice}\", not a door"));
                }
            }

            foreach (var c in r.AirlockWalls())
            {
                if (At(c).Edifice != "Wall")
                {
                    output.Add(string.Create(CultureInfo.InvariantCulture, $"airlock {r.Id} side wall {c} holds \"{At(c).Edifice}\", not a wall"));
                }
            }
        }

        output.Sort(StringComparer.Ordinal);
        return output;
    }

    private static bool Adjacent(Cell a, Cell b)
    {
        int dx = a.X - b.X, dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz) == 1;
    }

    /// <summary>Fails a plan with no airlock: the case asserts the airlocks the tiler chose, and on a very cold map it chooses at least one.</summary>
    private static AcceptanceException? AirlockCount(LayoutPlan plan) =>
        plan.Rooms.Any(r => r.Role == PlannedRole.Airlock) ? null : new AcceptanceException("the plan holds no airlock on a very cold map");

    /// <summary>Fails a non-outdoor planned room with a floor cell outside the roofed set.</summary>
    private static AcceptanceException? UnroofedRooms(LayoutPlan plan)
    {
        var failures = new List<Exception?>();
        foreach (var r in plan.Rooms)
        {
            if (r.Role.IsOutdoor())
            {
                continue;
            }

            foreach (var c in r.Region.Cells())
            {
                if (!plan.Roofed.Contains(c))
                {
                    failures.Add(new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{Wire.Name(r.Role)} room {r.Id} floor cell {c} is outside the roofed set")));
                    break;
                }
            }
        }

        return Join([.. failures]);
    }

    /// <summary>
    /// Fails a roofed room the door graph does not join to the first one: the walk crosses only roofed cells and door
    /// cells (a room's doors and the plan's link doors), never an open cell outside the roofed set.
    /// </summary>
    private static AcceptanceException? DisconnectedRooms(LayoutPlan plan)
    {
        var walk = new HashSet<Cell>(plan.Roofed.Cells());
        foreach (var r in plan.Rooms)
        {
            foreach (var c in plan.DoorsOf(r))
            {
                walk.Add(c);
            }
        }

        var rooms = plan.Rooms.Where(r => !r.Role.IsOutdoor() && !r.Region.Empty() && plan.Roofed.Contains(r.Region.Cells()[0])).ToList();
        if (rooms.Count == 0)
        {
            return new AcceptanceException("the plan holds no roofed room");
        }

        var start = rooms[0].Region.Cells()[0];
        var reached = new HashSet<Cell> { start };
        for (var queue = new Queue<Cell>([start]); queue.Count > 0;)
        {
            var c = queue.Dequeue();
            foreach (var n in new[] { new Cell(c.X + 1, c.Z), new Cell(c.X - 1, c.Z), new Cell(c.X, c.Z + 1), new Cell(c.X, c.Z - 1) })
            {
                if (walk.Contains(n) && reached.Add(n))
                {
                    queue.Enqueue(n);
                }
            }
        }

        var failures = new List<Exception?>();
        foreach (var r in rooms)
        {
            if (!reached.Contains(r.Region.Cells()[0]))
            {
                failures.Add(new AcceptanceException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Wire.Name(r.Role)} room {r.Id} is not joined to {Wire.Name(rooms[0].Role)} room {rooms[0].Id} over roofed cells and doors")));
            }
        }

        return Join([.. failures]);
    }

    /// <summary>
    /// Go <c>solo.AuditColonists</c> (also the layout/sea-ice-envelope case's probe): holds every initial colonist to alive
    /// with Hypothermia at or below <see cref="HypothermiaCap"/>. A corpse can despawn, so a missing pawn fails too.
    /// </summary>
    private static AcceptanceException? AuditColonists(IReadOnlyList<JsonObject> listed, IReadOnlyList<string> initial, JsonObject row)
    {
        var alive = new Dictionary<string, bool>(StringComparer.Ordinal);
        var worst = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pawn in listed)
        {
            var id = Colonists.RowID(pawn);
            alive[id] = pawn["dead"].AsBool() is false;
            foreach (var entry in pawn["health"].AsMap()?["hediffs"].AsSlice() ?? [])
            {
                var hediff = entry.AsMap() ?? [];
                if (Colonists.HediffDef(hediff) == "Hypothermia")
                {
                    worst[id] = Math.Max(worst.GetValueOrDefault(id), hediff["severity"].AsNumber());
                }
            }
        }

        var worstRow = new JsonObject();
        foreach (var (id, severity) in worst.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            worstRow[id] = severity;
        }

        row["hypothermia"] = worstRow;
        var failures = new List<Exception?>();
        foreach (var id in initial)
        {
            if (!alive.GetValueOrDefault(id))
            {
                failures.Add(new AcceptanceException($"colonist {id} dead or gone"));
            }
            else if (worst.GetValueOrDefault(id) > HypothermiaCap)
            {
                failures.Add(new AcceptanceException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"colonist {id} Hypothermia {worst.GetValueOrDefault(id):F2} past the {HypothermiaCap:F2} cap")));
            }
        }

        return Join([.. failures]);
    }

    /// <summary>Go <c>errors.Join</c>: the non-nil failures as one, newline separated; null when none.</summary>
    private static AcceptanceException? Join(params Exception?[] failures)
    {
        var real = failures.OfType<Exception>().ToList();
        return real.Count switch
        {
            0 => null,
            1 when real[0] is AcceptanceException single => single,
            _ => new AcceptanceException(string.Join('\n', real.Select(e => e.Message)), real[0]),
        };
    }
}
