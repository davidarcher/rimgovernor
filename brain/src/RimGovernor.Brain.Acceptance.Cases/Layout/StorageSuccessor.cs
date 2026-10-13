using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Journal = RimGovernor.Brain.Store.Store;
using ReviewRecord = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/storage-successor</c> (#2893, Go <c>storage_successor.go</c>): a standing storage room holding stock is
/// outgrown, the tiled replan sizes a larger successor beside it (EmbeddedBlock.Replaces, #3020), and the stockpile
/// zone follows the room (#2866) without ever leaving the stock unzoned.
/// <para>
/// Staging (planstage, as clearance/pack-reinstall): an inert controller derives the plan on the lab map; the plan's
/// first storage room is put on the ground finished (walls, door and roof) with Steel lying inside it; the real
/// controller then runs in probe-sized segments. Once a stockpile zone covers the stock, the lever is applied: the
/// harness-only stock fault (<c>FaultsEnv</c> "stock=Steel:n") raises the stock target the station chain sizes the
/// storage group for, so GroupOutgrown holds for the standing room.
/// </para>
/// <para>
/// Probes: every segment is <see cref="ProbeTicks"/> of play (two game hours); the controller is stopped, the harness
/// reattached and one serialized read of the zones and the map cells taken. A gap shorter than a segment is not
/// observable, so "never absent" means at every probe.
/// </para>
/// <para>
/// Asserted: (1) the plan holds a storage room whose Replaces is the old room's id; (2) at every probe after the
/// lever, while stock lies in the old or the successor room, some stockpile zone holds it; (3) the successor's zone
/// carries the old zone's filter and priority; (4) at the end the old room has left the plan, the old zone is gone and
/// all stock in the two rooms sits in the new zone.
/// </para>
/// </summary>
internal static class LayoutStorageSuccessor
{
    /// <summary>The probe cadence: two game hours a segment.</summary>
    private const long ProbeTicks = 5000;

    /// <summary>Bounds the lever to the old room leaving the plan: about five game days.</summary>
    private const int Segments = 60;

    /// <summary>The staged stock and the faulted resource; a Steel stack is 75, so the lever's 30000 is 400 stacks of shelving.</summary>
    private const string StockDef = "Steel";

    private const int StockCount = 150;

    private const int Lever = 30000;

    /// <summary>The stock target phase one plans against: enough for the plan to hold a storage room, far below the lever.</summary>
    private const int Seed = 1500;

    private static readonly string[] SpecFamilies = ["shelter", "expansion", "supply", "work"];

    internal static AcceptanceCase Case() => new()
    {
        Name = "layout/storage-successor",
        Scope = "Issue #2893 (epic #2855): a staged storage room holding Steel is outgrown (the stock fault raises the station chain's stock target) and the tiled replan " +
            "plans a successor (Replaces = the old id); at every two-game-hour probe a stockpile zone holds the stock, the successor's zone carries the old " +
            "filter and priority, and at the end the stock sits in the new zone, the old zone is gone and the old room has left the plan. Nightly only.",
        Start = new LabStart(),
        RequiredOps = [PlanStage.Tool],
        Serve = new ServeSpec
        {
            NativeTimeout = TimeSpan.FromSeconds(60),
            Prefix = "storage-successor",
            Families = SpecFamilies,
            Env = [StockFault(Seed)],
        },
        Budget = TimeSpan.FromMinutes(45),
        Crew = new Crew { Size = 3 },
        NoCheckpoint = true,
        Reason = "one serve launch per probe: the zone and the stock are native reads the controller's slot excludes, and the successor is built and the old room " +
            "cleared over game days; the staged room cannot be outgrown without that play",
        Run = RunAsync,
    };

    /// <summary>A stockpile zone as the game reports it.</summary>
    private sealed record ZoneRead(string Id, string Priority, IReadOnlyList<string> Allowed, IReadOnlyList<Cell> Cells)
    {
        internal bool Covers(Region r) => Cells.Any(r.Contains);

        internal JsonObject ToJson() => new()
        {
            ["id"] = Id,
            ["priority"] = Priority,
            ["allowed"] = new JsonArray([.. Allowed.Select(a => (JsonNode?)a)]),
            ["cells"] = Cells.Count,
        };
    }

    private static bool SameSettings(ZoneRead a, ZoneRead b) =>
        string.Equals(a.Priority, b.Priority, StringComparison.Ordinal) && a.Allowed.SequenceEqual(b.Allowed, StringComparer.Ordinal);

    /// <summary>The cells of the given rooms that hold stock, by the id of the zone covering each (empty for a loose stack).</summary>
    private static Dictionary<Cell, string> StockIn(HashSet<Cell> held, IReadOnlyList<ZoneRead> zones, params Region[] rooms)
    {
        var zoneOf = new Dictionary<Cell, string>();
        foreach (var z in zones)
        {
            foreach (var c in z.Cells)
            {
                zoneOf[c] = z.Id;
            }
        }

        var output = new Dictionary<Cell, string>();
        foreach (var room in rooms)
        {
            foreach (var c in room.Cells())
            {
                if (held.Contains(c))
                {
                    output[c] = zoneOf.GetValueOrDefault(c, "");
                }
            }
        }

        return output;
    }

    /// <summary>Assertion 2 at one probe: stock in the rooms with no zone holding any of it is the zone being absent while stock is held.</summary>
    private static void NeverAbsent(Dictionary<Cell, string> stock)
    {
        if (stock.Count == 0 || stock.Values.Any(id => id.Length > 0))
        {
            return;
        }

        throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{stock.Count} cells hold stock and no stockpile zone covers any"));
    }

    /// <summary>The storage room of the plan that replaces room <paramref name="id"/>.</summary>
    private static PlannedRoom? SuccessorOf(LayoutPlan plan, int id) =>
        plan.Rooms.FirstOrDefault(r => r.Role == PolicyFunctions.PlannedStorage && r.Replaces == id);

    private static bool InPlan(LayoutPlan plan, int id) => plan.Rooms.Any(r => r.Id == id);

    /// <summary>
    /// Assertions 3 and 4 over a probe: the zone over the successor (other than the old one) carries the old settings,
    /// the old zone is gone and every stock cell of the two rooms is in the new zone. It returns the new zone and the
    /// failures; an empty failure list is a finished hand-off.
    /// </summary>
    private static (ZoneRead Fresh, List<string> Failures) Handoff(ZoneRead old, IReadOnlyList<ZoneRead> zones, Region successor, Dictionary<Cell, string> stock)
    {
        var fresh = new ZoneRead("", "", [], []);
        foreach (var z in zones)
        {
            if (!string.Equals(z.Id, old.Id, StringComparison.Ordinal) && z.Covers(successor))
            {
                fresh = z;
                break;
            }
        }

        var output = new List<string>();
        foreach (var z in zones)
        {
            if (string.Equals(z.Id, old.Id, StringComparison.Ordinal))
            {
                output.Add($"old zone {old.Id} still stands");
            }
        }

        if (fresh.Id.Length == 0)
        {
            output.Add("no zone covers the successor room");
        }
        else if (!SameSettings(old, fresh))
        {
            output.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"new zone {fresh.Id} carries filter {fresh.Allowed.Count} defs priority {fresh.Priority}, the old zone's {old.Allowed.Count} defs priority {old.Priority}"));
        }

        if (stock.Count == 0)
        {
            output.Add("no stock lies in either room");
        }

        foreach (var (c, id) in stock.OrderBy(e => e.Key.Z).ThenBy(e => e.Key.X))
        {
            if (!string.Equals(id, fresh.Id, StringComparison.Ordinal))
            {
                output.Add($"stock at {c} is in zone \"{id}\", not the new zone \"{fresh.Id}\"");
                break;
            }
        }

        return (fresh, output);
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;

        // Phase one: an inert controller, its stock fault at the seed target, derives and records a layout plan that
        // holds a storage room.
        var one = await Phase.BeginAsync(s, s.Spec with { Families = ["work"] }, cancellationToken).ConfigureAwait(false);
        LayoutPlan plan;
        try
        {
            plan = await one.PlanAsync(
                "layout plan with a storage room",
                p => p.Rooms.Any(r => r.Role == PolicyFunctions.PlannedStorage),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await one.AbortAsync().ConfigureAwait(false);
            throw;
        }

        var old = plan.Rooms.First(r => r.Role == PolicyFunctions.PlannedStorage);
        var h = await one.StopAsync("plan_service", cancellationToken).ConfigureAwait(false);

        // Stage the room finished with Steel in it and a stockpile zone over it.
        var ring = PlanStage.RingOf(Geometry(plan, old));
        var inner = old.Region.Bounds();
        var zone = new StageRect(inner.X, inner.Z, inner.X + inner.Width - 1, inner.Z + inner.Height - 1);
        var staged = ring.Spec("WoodLog", "") with
        {
            Drops = [new StageDrop(StockDef, StockCount, zone.MinX, zone.MinZ)],
            Stockpile = zone,
        };
        _ = await PlanStage.StageAsync(h, "stage-room", staged, cancellationToken).ConfigureAwait(false);
        var audit = await PlanStage.ReadAsync(h, "audit-room", [], ring.Cells(), cancellationToken).ConfigureAwait(false);
        try
        {
            ring.Sealed(audit);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("the staged storage room is not a standing room: " + e.Message, e);
        }

        var (zones, held) = await ProbeZonesAsync(h, s, "baseline", cancellationToken).ConfigureAwait(false);
        ZoneRead? before = null;
        foreach (var z in zones)
        {
            if (z.Covers(old.Region))
            {
                before = z;
            }
        }

        if (before is null)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the staged room has no stockpile zone: {zones.Count} zones"));
        }

        if (StockIn(held, zones, old.Region).Count == 0)
        {
            throw new AcceptanceException("the staged room holds no stock");
        }

        report["staged"] = new JsonObject
        {
            ["room"] = old.Id,
            ["bounds"] = new JsonObject { ["x"] = inner.X, ["z"] = inner.Z, ["width"] = inner.Width, ["height"] = inner.Height },
            ["zone"] = before.ToJson(),
        };

        // Phase two: the real families with the lever applied, in probe-sized segments, until the old room has left the
        // plan and the hand-off finished.
        var spec = s.Spec with { Env = [StockFault(Lever)], Families = SpecFamilies };
        var failures = new List<string>();
        for (var segment = 0; segment < Segments; segment++)
        {
            var two = await Phase.BeginAsync(s, spec, cancellationToken).ConfigureAwait(false);
            long tick = 0;
            LayoutPlan? now = null;
            try
            {
                await two.UntilAsync(
                    "a probe of play",
                    review =>
                    {
                        if (tick == 0)
                        {
                            tick = review.Tick.Value;
                        }

                        if (two.Journal.LayoutPlan(review.Snapshot, review.Tick, cancellationToken)?.Plan is { } recorded)
                        {
                            now = recorded;
                        }

                        return ("plan", review.Tick.Value - tick >= ProbeTicks);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await two.AbortAsync().ConfigureAwait(false);
                throw;
            }

            h = await two.StopAsync(string.Create(CultureInfo.InvariantCulture, $"segment_{segment}"), cancellationToken).ConfigureAwait(false);
            (zones, held) = await ProbeZonesAsync(h, s, string.Create(CultureInfo.InvariantCulture, $"probe-{segment}"), cancellationToken).ConfigureAwait(false);
            var succ = now is null ? null : SuccessorOf(now, old.Id);
            var rooms = succ is null ? new[] { old.Region } : [old.Region, succ.Region];
            var stock = StockIn(held, zones, rooms);
            try
            {
                NeverAbsent(stock);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"probe {segment}: {e.Message}"), e);
            }

            if (succ is null || now is null || InPlan(now, old.Id))
            {
                continue;
            }

            var (fresh, failed) = Handoff(before, zones, succ.Region, stock);
            report["handoff"] = new JsonObject
            {
                ["segment"] = segment,
                ["successor"] = succ.Id,
                ["zone"] = fresh.ToJson(),
                ["failures"] = new JsonArray([.. failed.Select(f => (JsonNode?)f)]),
            };
            failures = failed;
            if (failed.Count == 0)
            {
                return;
            }
        }

        throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"no finished hand-off in {Segments} probes: {string.Join("; ", failures)}"));
    }

    /// <summary>The <c>FaultsEnv</c> entry raising the Steel stock target to <paramref name="n"/>.</summary>
    private static string StockFault(int n) =>
        string.Create(CultureInfo.InvariantCulture, $"{LayoutRing.FaultsEnv}=stock={StockDef}:{n}");

    /// <summary>The room's geometry for the staging ring: its role, interior, primary door and the plan's shell doors.</summary>
    private static PlannedRoomGeometry Geometry(LayoutPlan plan, PlannedRoom room)
    {
        var inner = room.Region.Bounds();
        return new PlannedRoomGeometry
        {
            Role = Wire.Name(room.Role),
            X = inner.X,
            Z = inner.Z,
            Width = inner.Width,
            Height = inner.Height,
            Door = new StageCell(room.Door.X, room.Door.Z),
            DoorRotation = PlanStage.RotationNumber(room.DoorRot?.ToString() ?? ""),
            ShellDoors = [.. plan.ShellDoors(room).Select(d => new StageCell(d.X, d.Z))],
        };
    }

    /// <summary>
    /// Reads the stockpile zones with their filters and cells, and the cells holding things, in two serialized calls plus
    /// the cell grid.
    /// </summary>
    private static async Task<(List<ZoneRead> Zones, HashSet<Cell> Held)> ProbeZonesAsync(Harness h, ISession s, string label, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label + "-zones",
            "observations_list_zones",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() }, ["includeFilter"] = true },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var grid = await h.MapCellsAsync(label + "-cells", s.Identity, cancellationToken).ConfigureAwait(false);
        var cells = grid.ZoneCells();
        var zones = new List<ZoneRead>();
        foreach (var raw in observed["zones"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (row["type"].AsString() != "stockpile")
            {
                continue;
            }

            var allowed = (row["filter"].AsMap()?["allowedDefNames"].AsSlice() ?? []).Select(d => d.AsString()).ToList();
            allowed.Sort(StringComparer.Ordinal);
            var id = row["id"].AsString();
            zones.Add(new ZoneRead(id, row["priority"].ToText(), allowed, cells.GetValueOrDefault(id) ?? []));
        }

        var held = new HashSet<Cell>();
        foreach (var c in grid.Cells())
        {
            if (c.StorageEmpty is false)
            {
                held.Add(c.Cell);
            }
        }

        return (zones, held);
    }

    /// <summary>One run of the controller over the staged game (Go <c>planstage.Phase</c> over a typed journal).</summary>
    private sealed class Phase
    {
        private readonly ISession _session;
        private readonly ServiceProcess _service;
        private readonly Wait _wait;

        private Phase(ISession session, ServiceProcess service, Journal journal)
        {
            _session = session;
            _service = service;
            Journal = journal;
            _wait = new Wait { Ceiling = PlanStage.Ceiling, Stall = Wait.StallBudget(), Terminal = service.Exited };
        }

        internal Journal Journal { get; }

        /// <summary>Launches the controller under <paramref name="spec"/> and takes authority.</summary>
        internal static async Task<Phase> BeginAsync(ISession session, ServeSpec spec, CancellationToken cancellationToken)
        {
            var service = await session.ServeAsync(spec, cancellationToken).ConfigureAwait(false);
            try
            {
                _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
                service.KeepAuthority(cancellationToken);
                var journal = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(session.Config), cancellationToken).ConfigureAwait(false);
                return new Phase(session, service, journal);
            }
            catch
            {
                _ = await service.StopAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Polls the journal until <paramref name="done"/>; the signature is the review's game day and what done reports
        /// through detail, so a broken phase stalls and a long build does not.
        /// </summary>
        internal async Task UntilAsync(string what, Func<ReviewRecord, (string Detail, bool Ok)> done, CancellationToken cancellationToken)
        {
            try
            {
                await _wait.WaitProgressAsync(
                    async ct =>
                    {
                        ReviewRecord review;
                        try
                        {
                            review = await Journal.LoadRoundsAsync(ct).ConfigureAwait(false);
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            _ = e;
                            return (Wait.Signature("no-review"), false);
                        }

                        var (detail, ok) = done(review);
                        return (Wait.Signature(detail, (ulong)review.Tick.Value / Wait.TicksPerDay), ok);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{what}: {e.Message}", e);
            }
        }

        /// <summary>Waits for the recorded layout plan to satisfy <paramref name="want"/> and returns it.</summary>
        internal async Task<LayoutPlan> PlanAsync(string what, Func<LayoutPlan, bool> want, CancellationToken cancellationToken)
        {
            LayoutPlan? plan = null;
            await UntilAsync(
                what,
                review =>
                {
                    if (review.Revision == 0)
                    {
                        // The controller has not reviewed yet: no world identity to scope the plan by.
                        return ("no-review", false);
                    }

                    if (Journal.LayoutPlan(review.Snapshot, review.Tick, cancellationToken) is not { Plan: { } recorded } record)
                    {
                        return ("no-plan", false);
                    }

                    if (!want(recorded))
                    {
                        return (string.Create(CultureInfo.InvariantCulture, $"plan-{record.Tick.Value}"), false);
                    }

                    plan = recorded;
                    return ("plan", true);
                },
                cancellationToken).ConfigureAwait(false);
            return plan!;
        }

        /// <summary>Ends the phase and takes the harness back, paused.</summary>
        internal async Task<Harness> StopAsync(string label, CancellationToken cancellationToken)
        {
            Journal.Dispose();
            _session.Report[label] = await _service.StopAsync().ConfigureAwait(false);
            var harness = _session.Reattach();
            _ = await harness.CallAsync("pause-" + label, "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
            return harness;
        }

        /// <summary>Ends a failed phase without taking the harness back.</summary>
        internal async Task AbortAsync()
        {
            Journal.Dispose();
            _ = await _service.StopAsync().ConfigureAwait(false);
        }
    }
}
