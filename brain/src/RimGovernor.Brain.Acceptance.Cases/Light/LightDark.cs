using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>light/dark</c> (Go <c>light/light.go</c>): the rooms/lighting Need vertical on the lab contract. An enclosed roofed
/// room on the blank lab holds a fuelled stove whose interaction cell native measures dark, with no lamp in reach. The
/// service must latch the bench from the measured glow, admit exactly one affordable lamp (a TorchLamp: the colony has no
/// power source) on a free cell of the room within the placement radius, the colonists build it, and the next measured
/// census must release the latch. An independent native read then confirms the cell reads lit and the lamp stands where
/// it was admitted.
/// </summary>
/// <remarks>
/// Parity notes against the Go case: the latch is observed as the concern's row in <c>/api/routines</c> and its release
/// as the measured glow itself (the player API exposes no latch sets), and the count of methods the goal ever committed
/// (<c>lighting_methods</c>) is not reported (the API has no goal method history). Uses the private disposable
/// <c>test/lighting_prepare</c> fixture (LightingFixture.cs).
/// </remarks>
internal static class LightDark
{
    private const string Prefix = "light-accept";

    internal static AcceptanceCase Case() => new()
    {
        Name = "light/dark",
        Tier = CaseTier.Smoke,
        Scope = "Native rooms/lighting Need vertical on the lab: a measured-dark stove interaction cell in an enclosed room "
            + "drives the live Go rounder/planner to admit one affordable lamp beside it; "
            + "the measured glow, not the receipt, releases the latch, confirmed by an independent native read.",
        Start = new FixtureStart { Op = "test/lighting_prepare", Args = new JsonObject { ["scenario"] = "dark" }, On = new LabStart() },
        Service = true,

        // ~2x the measured healthy run: the lamp is admitted in under a minute.
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        RoutineService? run = null;
        try
        {
            run = await BodyAsync(s, value => run = value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Failure evidence: the same independent lighting read a pass ends with, taken once the service has let go
            // of the game.
            if (!s.Report.Has("lighting_after"))
            {
                await RoutineService.PostmortemAsync(
                    s,
                    run,
                    async (ph, ct) =>
                    {
                        try
                        {
                            s.Report["lighting_postmortem"] = (await ReadAsync(ph, s.Identity, "lighting-postmortem", ct).ConfigureAwait(false)).Evidence();
                        }
                        catch (AcceptanceException e)
                        {
                            s.Report["lighting_postmortem_error"] = e.Message;
                        }
                    }).ConfigureAwait(false);
            }
        }
    }

    private static async Task<RoutineService?> BodyAsync(ISession s, Action<RoutineService> started, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var stoveId = prepared["stove"].AsString();
        var interior = prepared["interior"].AsMap() ?? [];
        var workCellMap = prepared["workCell"].AsMap() ?? [];
        var workCell = ((int)workCellMap["x"].AsNumber(), (int)workCellMap["z"].AsNumber());
        bool Inside((int X, int Z) c) =>
            c.X >= interior["minX"].AsNumber() && c.X <= interior["maxX"].AsNumber()
            && c.Z >= interior["minZ"].AsNumber() && c.Z <= interior["maxZ"].AsNumber();

        // Before: the typed lighting census must list the stove's interaction cell roofed and dark -- the exact facts the
        // review latches on -- and no lamp.
        var lighting = LightingPolicy.Default();
        var before = await ReadAsync(h, identity, "lighting-before", cancellationToken).ConfigureAwait(false);
        report["lighting_before"] = before.Evidence();
        if (!before.Cells.TryGetValue(stoveId, out var stove) || !stove.Roofed || stove.Glow >= lighting.LitGlow || stove.Cell != workCell)
        {
            throw new AcceptanceException($"lighting-before: fixture stove is not a roofed dark work cell: {stove}");
        }

        if (before.Lamps.Count != 0)
        {
            throw new AcceptanceException($"lighting-before: {before.Lamps.Count} lamps present before the controller acts");
        }

        if (stove.LightSensitive)
        {
            throw new AcceptanceException($"lighting-before: work cell marked light-sensitive without a cave plant: {stove}");
        }

        // "work" rides along because every building method's builder check requires the colony's work priorities to
        // match the controller's own assignment, which only the work family applies.
        var run = await RoutineService.StartAsync(s, Prefix, ["lighting", "work"], cancellationToken).ConfigureAwait(false);
        started(run);
        var concern = RoomsFamily.Lighting.Value;

        // The review must bind the rooms/lighting Need.
        _ = await run.WaitBoundAsync(concern, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);

        // One lighting method from commitment through the build to the measured census releasing the latch: one lamp
        // build on a free interior cell within the placement radius of the interaction cell, never on it, renewed across
        // incidental cancellations.
        var method = await RoutineService.BoundedAsync(ct => run.Service.WaitMethodAsync(concern, null, ct), TimeSpan.FromMinutes(8), "lighting method", cancellationToken).ConfigureAwait(false);

        report["concern_id"] = method.Concern;
        (int X, int Z) builtCell;
        string builtDefinition;
        for (var renewals = 0; ; renewals++)
        {
            var plan = await run.LoadPlanAsync(method.Plan, cancellationToken).ConfigureAwait(false);
            var actions = plan["actions"].AsSlice();
            if (actions.Count != 1)
            {
                throw new AcceptanceException($"lighting plan {method.Plan} has {actions.Count} actions, expected 1");
            }

            var b = RoutineService.Building(actions[0])
                ?? throw new AcceptanceException($"lighting plan action is not a building: {actions[0].ToJson()}");
            builtCell = ((int)b["x"].AsNumber(), (int)b["z"].AsNumber());
            builtDefinition = b["defName"].AsString();
            if (builtDefinition != "TorchLamp")
            {
                throw new AcceptanceException($"lighting admitted {builtDefinition}; a colony without a power source must choose the TorchLamp");
            }

            if (builtCell == workCell || !Inside(builtCell) || Math.Max(Math.Abs(builtCell.X - workCell.Item1), Math.Abs(builtCell.Z - workCell.Item2)) > lighting.PlacementRadius)
            {
                throw new AcceptanceException($"lamp placed at {builtCell}: not a free interior cell within {lighting.PlacementRadius} of the work cell {workCell}");
            }

            report["lamp_cell"] = new JsonObject { ["x"] = builtCell.X, ["z"] = builtCell.Z, ["definition"] = builtDefinition };
            var planId = method.Plan;
            var (state, incidental) = await RoutineService.BoundedAsync(ct => run.Service.WaitPlanTerminalAsync(planId, ct), TimeSpan.FromMinutes(10), "lighting plan", cancellationToken).ConfigureAwait(false);

            if (!incidental)
            {
                report["lighting_plan"] = method.Plan;
                report["lighting_completed_tick"] = RoutineService.CompletedTick(state);
                report["incidental_renewals"] = renewals;
                break;
            }

            method = await RoutineService.BoundedAsync(
                ct => run.Service.WaitMethodAsync(concern, planId, ct),
                TimeSpan.FromMinutes(5),
                string.Create(CultureInfo.InvariantCulture, $"renewed lighting method after incidental cancellation #{renewals + 1}"),
                cancellationToken).ConfigureAwait(false);
        }

        // The measured census releases the latch once the cell reads lit.
        await run.WaitCensusAsync(
            $"the lighting census never released the latch on {stoveId}",
            TimeSpan.FromMinutes(5),
            async ct =>
            {
                var now = await ReadAsync(h, identity, "lighting-release", ct).ConfigureAwait(false);
                var glow = now.Cells.TryGetValue(stoveId, out var row) ? row.Glow : 0;
                return (Wait.Signature(glow, now.Lamps.Count), glow >= lighting.LitGlow);
            },
            cancellationToken).ConfigureAwait(false);
        await run.WaitRunningAsync("lighting", cancellationToken).ConfigureAwait(false);

        // Independent native read after the service releases the game slot.
        h = await run.ReleaseAsync(s, cancellationToken).ConfigureAwait(false);
        var after = await ReadAsync(h, identity, "lighting-after", cancellationToken).ConfigureAwait(false);
        report["lighting_after"] = after.Evidence();
        try
        {
            CheckLit(after, stoveId, builtCell, builtDefinition, lighting.LitGlow);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("lighting-after: " + e.Message, e);
        }

        return run;
    }

    // The independent read the case ends on: the stove cell lit and exactly the admitted lamp standing lit where it was placed.
    private static void CheckLit(Summary after, string stoveId, (int X, int Z) cell, string definition, double litGlow)
    {
        var glow = after.Cells.TryGetValue(stoveId, out var stove) ? stove.Glow : 0;
        if (stove is null || glow < litGlow)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"stove cell glow {glow:F2} is still under {litGlow:F2}"));
        }

        if (after.Lamps.Count != 1)
        {
            throw new AcceptanceException($"expected exactly 1 lamp, observed {after.Lamps.Count}: {after.Evidence()["lamps"].ToJson()}");
        }

        foreach (var lamp in after.Lamps.Values)
        {
            if (lamp.Cell != cell || lamp.Definition != definition || !lamp.Lit)
            {
                throw new AcceptanceException($"the surviving lamp {lamp} is not the lit {definition} admitted at {cell}");
            }
        }
    }

    private sealed record WorkCellRow((int X, int Z) Cell, double Glow, bool Roofed, bool LightSensitive);

    private sealed record LampRow(string Definition, (int X, int Z) Cell, double Radius, bool Lit, bool Powered);

    private sealed class Summary
    {
        public Dictionary<string, WorkCellRow> Cells { get; } = [];

        public Dictionary<string, LampRow> Lamps { get; } = [];

        public JsonObject Evidence()
        {
            var cells = new JsonObject();
            foreach (var (id, c) in Cells)
            {
                cells[id] = new JsonObject { ["x"] = c.Cell.X, ["z"] = c.Cell.Z, ["glow"] = c.Glow, ["roofed"] = c.Roofed, ["light_sensitive"] = c.LightSensitive };
            }

            var lamps = new JsonObject();
            foreach (var (id, l) in Lamps)
            {
                lamps[id] = new JsonObject { ["definition"] = l.Definition, ["x"] = l.Cell.X, ["z"] = l.Cell.Z, ["glow_radius"] = l.Radius, ["lit"] = l.Lit, ["powered"] = l.Powered };
            }

            return new JsonObject { ["work_cells"] = cells, ["lamps"] = lamps };
        }
    }

    /// <summary>Decodes the typed upkeep lighting section: work cells keyed by bench id, lamps keyed by building id.</summary>
    private static async Task<Summary> ReadAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        var facts = await RoutineService.ReadUpkeepAsync(h, identity, label, "lighting", cancellationToken).ConfigureAwait(false);
        var summary = new Summary();
        foreach (var raw in facts["workCells"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var bench = row["bench"].AsMap() ?? [];
            var cell = row["cell"].AsMap() ?? [];
            summary.Cells[bench["id"].AsString()] = new WorkCellRow(
                ((int)cell["x"].AsNumber(), (int)cell["z"].AsNumber()),
                row["glow"].AsNumber(),
                row["roofed"].AsBool() == true,
                row["lightSensitive"].AsBool() == true);
        }

        var rows = await h.BuildingRowsAsync(label + "-buildings", identity, cancellationToken).ConfigureAwait(false);
        foreach (var raw in facts["lamps"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var id = row["building"].AsMap()?["id"].AsString() ?? "";
            if (!rows.TryGetValue(id, out var building))
            {
                throw new AcceptanceException($"{label}: lamp {id} is not in the building table");
            }

            var head = building["building"].AsMap() ?? [];
            var position = head["position"].AsMap() ?? [];
            var service = building["service"].AsMap() ?? [];
            summary.Lamps[id] = new LampRow(
                head["defName"].AsString(),
                ((int)position["x"].AsNumber(), (int)position["z"].AsNumber()),
                row["glowRadius"].AsNumber(),
                row["lit"].AsBool() == true,
                service["powerOn"].AsBool() == true);
        }

        return summary;
    }
}
