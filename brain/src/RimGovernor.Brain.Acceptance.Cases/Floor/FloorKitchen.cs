using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using DomainWire = RimGovernor.Brain.Domain.Wire;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>floor/kitchen</c> (Go <c>floor/floor.go</c>): the MaintainFlooring vertical. A live game and the brain composed
/// with the flooring family. An enclosed roofed kitchen (a fuelled stove) stands on bare soil, whose terrain
/// cleanliness native measures negative. The service must latch the room from the measured census, admit an affordable
/// floor from the policy's list on the deficient interior cells only, the colonists lay it, and the next measured
/// census must release the latch. An independent native read then confirms every interior cell reads a laid,
/// non-negative-cleanliness terrain.
/// </summary>
/// <remarks>
/// Uses the private disposable <c>test/flooring_prepare</c> fixture (FlooringFixture.cs). Parity notes against the Go
/// case: the latch is observed as the concern's row in <c>/api/routines</c> and its release as the measured census
/// itself (the API exposes no latch sets), and the deficient-cell check reads native, not the journal.
/// </remarks>
internal static class FloorKitchen
{
    private const string Prefix = "floor-accept";

    internal static AcceptanceCase Case() => new()
    {
        Name = "floor/kitchen",
        Scope = "Native MaintainFlooring vertical: a measured-deficient kitchen interior on bare soil drives the live Go "
            + "rounder/planner to admit an affordable floor on the deficient cells only; the colonists lay it and "
            + "the measured census, not the receipt, releases the latch, confirmed by an independent native read.",
        Start = new FixtureStart { On = new LabStart(), Op = "test/flooring_prepare" },
        Service = true,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private sealed class Held
    {
        public RoutineService? Run { get; set; }

        public CellRect Interior { get; set; }
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var held = new Held();
        try
        {
            await BodyAsync(s, held, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Failure evidence: the same independent flooring read a pass ends with.
            if (!s.Report.Has("flooring_after"))
            {
                await RoutineService.PostmortemAsync(
                    s,
                    held.Run,
                    async (ph, ct) =>
                    {
                        try
                        {
                            var after = await ReadAsync(ph, s.Identity, "flooring-postmortem", held.Interior, ct).ConfigureAwait(false);
                            s.Report["flooring_postmortem"] = after.Evidence();
                        }
                        catch (AcceptanceException e)
                        {
                            s.Report["flooring_postmortem_error"] = e.Message;
                        }
                    }).ConfigureAwait(false);
            }
        }
    }

    private static async Task BodyAsync(ISession s, Held held, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var roomId = prepared["roomId"].AsString();
        var rect = prepared["interior"].AsMap() ?? [];
        var interior = new CellRect((int)rect["minX"].AsNumber(), (int)rect["minZ"].AsNumber(), (int)rect["maxX"].AsNumber(), (int)rect["maxZ"].AsNumber());
        held.Interior = interior;
        var flooring = PolicyFunctions.DefaultFlooringPolicy();
        var allowed = new HashSet<string>(flooring.Floors, StringComparer.Ordinal);

        // Before: the typed flooring census must list the fixture room as a kitchen whose every interior cell stands on
        // natural ground with a negative terrain cleanliness -- the exact facts the review latches on.
        var before = await ReadAsync(h, identity, "flooring-before", interior, cancellationToken).ConfigureAwait(false);
        report["flooring_before"] = before.Evidence();
        if (before.RoomId != roomId || before.Role != DomainWire.Name(RoomRole.Kitchen))
        {
            throw new AcceptanceException($"flooring-before: fixture room {before.RoomId}/{before.Role} is not the prepared kitchen {roomId}");
        }

        if (before.Cells.Count != interior.Area || before.Deficient != interior.Area || before.Pending != 0)
        {
            throw new AcceptanceException($"flooring-before: {before.Deficient} of {before.Cells.Count} interior cells read deficient, {before.Pending} pending");
        }

        // "work" rides along because every building method's builder check requires the colony's work priorities to
        // match the controller's own assignment, which only the work family applies.
        var run = await RoutineService.StartAsync(s, Prefix, ["flooring", "work"], cancellationToken).ConfigureAwait(false);
        held.Run = run;
        var concern = PolicyFunctions.MaintainFlooring.Value;

        // The review must bind MaintainFlooring.
        _ = await run.WaitBoundAsync(concern, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);

        // The flooring methods: each plan lays one policy floor on deficient interior cells only, never twice on a cell,
        // until the measured census releases the room. Incidental cancellations renew the method.
        string? previous = null;
        var plans = new JsonArray();
        var laid = new Dictionary<(int X, int Z), string>();
        var definition = "";
        var released = false;
        var renewals = 0;
        while (!released && plans.Count < 8)
        {
            var followed = previous;
            var method = await RoutineService.BoundedAsync(ct => run.Service.WaitMethodAsync(concern, followed, ct), TimeSpan.FromMinutes(8), "flooring method", cancellationToken).ConfigureAwait(false);

            report["concern_id"] = method.Concern;
            previous = method.Plan;
            var plan = await run.LoadPlanAsync(method.Plan, cancellationToken).ConfigureAwait(false);
            var actions = plan["actions"].AsSlice();
            if (actions.Count == 0 || actions.Count > flooring.MaxCellsPerPlan)
            {
                throw new AcceptanceException($"flooring plan {method.Plan} has {actions.Count} actions");
            }

            var batch = new HashSet<(int X, int Z)>();
            foreach (var action in actions)
            {
                var b = RoutineService.Building(action)
                    ?? throw new AcceptanceException($"flooring plan action is not a building: {action.ToJson()}");
                var name = b["defName"].AsString();
                if (!allowed.Contains(name) || (definition.Length > 0 && name != definition))
                {
                    throw new AcceptanceException($"flooring admitted {name}; expected one floor from [{string.Join(' ', flooring.Floors)}]");
                }

                definition = name;
                var cell = ((int)b["x"].AsNumber(), (int)b["z"].AsNumber());
                if (!interior.Contains(cell) || !batch.Add(cell))
                {
                    throw new AcceptanceException($"floor placed at {cell}: not a unique interior cell of {interior}");
                }
            }

            var (state, incidental) = await RoutineService.BoundedAsync(ct => run.Service.WaitPlanTerminalAsync(method.Plan, ct), TimeSpan.FromMinutes(10), "flooring plan", cancellationToken).ConfigureAwait(false);

            if (incidental)
            {
                renewals++;
                report["incidental_renewals"] = renewals;
                continue;
            }

            foreach (var cell in batch)
            {
                if (laid.TryGetValue(cell, out var earlier))
                {
                    throw new AcceptanceException($"floor at {cell} ordered twice ({earlier} then {definition})");
                }

                laid[cell] = definition;
            }

            plans.Add(method.Plan);
            report["flooring_plans"] = plans.DeepClone();
            report["flooring_completed_tick"] = RoutineService.CompletedTick(state);
            report["cells_laid"] = laid.Count;

            // The measured census releases the latch once no cell reads deficient; a bounded wait tells a partial batch
            // from a stall.
            try
            {
                await run.WaitCensusAsync(
                    $"the flooring census never released room {interior.MinX},{interior.MinZ}",
                    TimeSpan.FromMinutes(4),
                    async ct =>
                    {
                        var now = await ReadAsync(h, identity, "flooring-release", interior, ct).ConfigureAwait(false);
                        return (Wait.Signature(now.Deficient, now.Pending), now.Deficient == 0 && now.Pending == 0);
                    },
                    cancellationToken).ConfigureAwait(false);
                released = true;
            }
            catch (AcceptanceException e) when (laid.Count < interior.Area && !cancellationToken.IsCancellationRequested)
            {
                report["partial_release_wait"] = e.Message;
            }
        }

        if (!released)
        {
            throw new AcceptanceException($"room {interior.MinX},{interior.MinZ} still deficient after {plans.Count} plans laid {laid.Count} cells");
        }

        report["floor_definition"] = definition;
        await Rounds.AssertRoundsRunningAsync(run.Service.GetAsync, cancellationToken).ConfigureAwait(false);

        // Independent native read after the service releases the game slot.
        h = await run.ReleaseAsync(s, cancellationToken).ConfigureAwait(false);
        var after = await ReadAsync(h, identity, "flooring-after", interior, cancellationToken).ConfigureAwait(false);
        report["flooring_after"] = after.Evidence();
        if (after.Cells.Count != interior.Area || after.Deficient != 0 || after.Pending != 0)
        {
            throw new AcceptanceException($"flooring-after: {after.Deficient} of {after.Cells.Count} interior cells still deficient, {after.Pending} pending");
        }

        foreach (var (cell, row) in after.Cells)
        {
            laid.TryGetValue(cell, out var layer);
            if (row.Terrain != definition || layer != definition)
            {
                throw new AcceptanceException($"flooring-after: cell {cell} reads {row.Terrain}; the admitted {definition} was laid on {layer}");
            }
        }
    }

    internal readonly record struct CellRect(int MinX, int MinZ, int MaxX, int MaxZ)
    {
        public int Area => (MaxX - MinX + 1) * (MaxZ - MinZ + 1);

        public bool Contains((int X, int Z) c) => c.X >= MinX && c.X <= MaxX && c.Z >= MinZ && c.Z <= MaxZ;

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{{{MinX},{MinZ} - {MaxX},{MaxZ}}}");
    }

    internal sealed record CellRow(string Terrain, double Cleanliness, bool Natural, string Pending);

    internal sealed class Summary
    {
        public string RoomId { get; init; } = "";

        public string Role { get; init; } = "";

        public Dictionary<(int X, int Z), CellRow> Cells { get; } = [];

        public int Pending { get; set; }

        /// <summary>The interior cells short of the clean tier: negative terrain cleanliness, the kitchen's requirement.</summary>
        public int Deficient => Cells.Values.Count(row => row.Cleanliness < 0);

        public JsonObject Evidence()
        {
            var cells = new JsonArray();
            foreach (var (cell, row) in Cells.OrderBy(c => c.Key.Z).ThenBy(c => c.Key.X))
            {
                cells.Add(new JsonObject
                {
                    ["x"] = cell.X,
                    ["z"] = cell.Z,
                    ["terrain"] = row.Terrain,
                    ["cleanliness"] = row.Cleanliness,
                    ["natural"] = row.Natural,
                    ["pending"] = row.Pending,
                });
            }

            return new JsonObject { ["room_id"] = RoomId, ["role"] = Role, ["cells"] = cells, ["pending"] = Pending, ["deficient"] = Deficient };
        }
    }

    /// <summary>
    /// Decodes the typed upkeep flooring section the way the projection does, keeping the room that holds the fixture
    /// interior with each of its interior cells joined to its terrain's census stats.
    /// </summary>
    internal static async Task<Summary> ReadAsync(Harness h, JsonObject identity, string label, CellRect interior, CancellationToken cancellationToken)
    {
        var facts = await RoutineService.ReadUpkeepAsync(h, identity, label, "flooring", cancellationToken).ConfigureAwait(false);
        var terrains = new Dictionary<string, (double Cleanliness, bool Natural)>(StringComparer.Ordinal);
        foreach (var raw in facts["terrains"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            terrains[row["defName"].AsString()] = (row["cleanliness"].AsNumber(), row["natural"].AsBool() == true);
        }

        foreach (var raw in facts["rooms"].AsSlice())
        {
            var room = raw.AsMap() ?? [];
            var summary = new Summary { RoomId = room["room"].AsMap()?["id"].AsString() ?? "", Role = room["role"].AsString() };
            foreach (var rawCell in room["cells"].AsSlice())
            {
                var row = rawCell.AsMap() ?? [];
                var cell = row["cell"].AsMap() ?? [];
                var c = ((int)cell["x"].AsNumber(), (int)cell["z"].AsNumber());
                if (!interior.Contains(c))
                {
                    continue;
                }

                var name = row["terrain"].AsString();
                if (!terrains.TryGetValue(name, out var terrain))
                {
                    throw new AcceptanceException($"{label}: cell {c} names terrain \"{name}\" missing from the census table");
                }

                var pending = row["pending"].AsString();
                if (pending.Length > 0)
                {
                    summary.Pending++;
                }

                summary.Cells[c] = new CellRow(name, terrain.Cleanliness, terrain.Natural, pending);
            }

            if (summary.Cells.Count > 0)
            {
                return summary;
            }
        }

        throw new AcceptanceException($"{label}: no flooring room holds the fixture interior {interior}");
    }
}
