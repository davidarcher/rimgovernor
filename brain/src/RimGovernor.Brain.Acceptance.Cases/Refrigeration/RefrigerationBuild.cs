using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using DomainWire = RimGovernor.Brain.Domain.Wire;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>refrigeration/build</c> (Go <c>refrigeration/refrigeration.go</c>): native cooler construction, cooling and spoilage
/// recovery. A roofed room starts with warm meat and no cooler. The service must admit one wall cooler with its hot side
/// outdoors; colonists build it and the room cools below the review exit threshold. A partially rotted meat stack must
/// then remain below 0 C without further rot progress over a measured window. Snapshot tests cover setpoint, power and
/// season decisions.
/// </summary>
/// <remarks>
/// Parity notes against the Go case: the latch is observed as the concern's row in <c>/api/routines</c> and its release
/// as the measured food temperature reaching the policy's exit threshold within the two-game-day budget (the player API
/// exposes no latch sets), and the count of methods the goal ever committed (<c>refrigeration_methods</c>) is not
/// reported (the API has no goal method history).
/// </remarks>
internal static class RefrigerationBuild
{
    private const string Prefix = "refrigeration-accept";

    // Spoilage recovery: rot progress advances (in 250-tick intervals) one rot tick per game tick above ChilledMaxC,
    // proportionally between 0 and 10 C and not at all under 0 C. FreezeWindowTicks is one advance while the cooler pulls
    // the room from the release threshold to its freezer target, FreezeWindows bounds that wait (half a game day), and the
    // measured window is RotWindowSteps advances of RotStepTicks (one game hour, sampled at CompRottable's own 250-tick
    // cadence). The door still opens for the colonists' own traffic and a rare tick can land on the spike that lets in
    // (seen at 5 of 10 steps under a heat wave), so the window as a whole is the assertion: at least RotStoppedSteps steps
    // move the stack's progress by at most RotStoppedTolerance rot ticks (a rare tick within 0.1 C of zero), and the whole
    // window advances it by under RotWindowMaxFraction of its ticks against the one-per-tick warm rate: a room merely
    // chilled to ChilledExitC would read 50%, a warm one 100%.
    private const int FreezeWindowTicks = 2500;
    private const int FreezeWindows = 12;
    private const int RotStepTicks = 250;
    private const int RotWindowSteps = 10;
    private const int RotStoppedSteps = 5;
    private const double RotStoppedTolerance = 2.5;
    private const double RotWindowMaxFraction = 0.25;

    internal static AcceptanceCase Case() => new()
    {
        Name = "refrigeration/build",
        Scope = "Native MaintainRefrigeration vertical on the lab: warm at-risk meat in an enclosed room drives the live Go "
            + "rounder/planner to admit a Cooler on a vented wall; native cooling then takes the measured room under "
            + "the exit threshold, confirmed by an independent native read, and the seeded rotting stack's rot progress "
            + "stops advancing.",
        Start = new FixtureStart
        {
            Op = "test/refrigeration_prepare",
            Args = new JsonObject
            {
                ["existingCooler"] = false,
                ["disconnected"] = false,
                ["roomTemperatureC"] = 30,
                ["season"] = false,
                ["coolerTargetC"] = 21.0,
                ["foodDef"] = "Meat_Muffalo",
                ["rotStacks"] = 1,
                ["rotProgressFraction"] = 0.25,
                ["roomWidth"] = 6,
                ["roomHeight"] = 4,
            },
            On = new LabStart(),
        },
        Service = true,
        Budget = TimeSpan.FromMinutes(8),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        RoutineService? run = null;
        try
        {
            await BodyAsync(s, value => run = value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Failure evidence: the same independent food read a pass ends with, so a dropped refrigeration latch can be
            // explained (stock hauled out, rotted, or genuinely chilled), and the emergency reviewer's own threat census,
            // so an unsafe_colony hold names the pawns behind it.
            if (!s.Report.Has("food_after"))
            {
                await RoutineService.PostmortemAsync(
                    s,
                    run,
                    async (ph, ct) =>
                    {
                        try
                        {
                            var after = await ReadFoodStorageAsync(ph, s.Identity, s.Prepared?["meat"].AsSlice() ?? [], "food-postmortem", ct).ConfigureAwait(false);
                            s.Report["food_postmortem"] = after.Evidence();
                        }
                        catch (AcceptanceException e)
                        {
                            s.Report["food_postmortem_error"] = e.Message;
                        }

                        try
                        {
                            var reply = await ph.WireAsync(
                                "threats-postmortem",
                                "observations_read_status",
                                new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() }, ["colonists"] = false, ["threats"] = true },
                                ct).ConfigureAwait(false);
                            s.Report["threats_postmortem"] = Harness.Outcome(reply, "observed").Value["threats"]?.DeepClone();
                        }
                        catch (AcceptanceException tolerated)
                        {
                            // The threat census is best-effort evidence.
                            Tolerated.Note("threat census", tolerated);
                        }
                    }).ConfigureAwait(false);
            }
        }
    }

    private static async Task BodyAsync(ISession s, Action<RoutineService> started, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var meat = prepared["meat"].AsSlice();
        var interior = prepared["interior"].AsMap() ?? [];
        var walls = new HashSet<(int X, int Z)>();
        foreach (var raw in prepared["walls"].AsSlice())
        {
            var w = raw.AsMap() ?? [];
            walls.Add(((int)w["x"].AsNumber(), (int)w["z"].AsNumber()));
        }

        bool Inside(Cell c) =>
            c.X >= interior["minX"].AsNumber() && c.X <= interior["maxX"].AsNumber()
            && c.Z >= interior["minZ"].AsNumber() && c.Z <= interior["maxZ"].AsNumber();

        // Before: the typed colony facts must show the meat warm, roofed, in the fixture room and short of runway -- the
        // exact facts the review latches on.
        var before = await ReadFoodStorageAsync(h, identity, meat, "food-before", cancellationToken).ConfigureAwait(false);
        report["food_before"] = before.Evidence();
        if (before.WarmStacks == 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"food-before: fixture meat is not warm at-risk stock (warm stacks 0/{before.Rows}, temperature {before.Temperature:F1} C)"));
        }

        var rotting = prepared["rotting"].AsMap() ?? [];
        var rotId = rotting["id"].AsString();
        if (rotId.Length == 0)
        {
            throw new AcceptanceException($"fixture spawned no part-rotted meat stack: {prepared["rotting"].ToJson()}");
        }

        var rotBefore = await ReadRotAsync(h, rotId, "rot-before", cancellationToken).ConfigureAwait(false);
        report["rot_before"] = rotBefore.Evidence();
        if (rotBefore.Destroyed || rotBefore.Stage != "Fresh" || rotBefore.Progress <= 0)
        {
            throw new AcceptanceException($"rot-before: seeded stack {rotId} is not fresh with rot progress ({rotBefore})");
        }

        // "work" rides along because every building method's builder check (comfortBuilderAvailable) requires the colony's
        // work priorities to match the controller's own assignment, which only the work family applies.
        var run = await RoutineService.StartAsync(s, Prefix, ["refrigeration", "work"], cancellationToken).ConfigureAwait(false);
        started(run);
        var concern = PolicyFunctions.MaintainRefrigeration.Value;

        // The review must bind MaintainRefrigeration.
        _ = await run.WaitBoundAsync(concern, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);

        // The refrigeration method: a Cooler build on a wall cell.
        var method = await RoutineService.BoundedAsync(ct => run.Service.WaitMethodAsync(concern, null, ct), TimeSpan.FromMinutes(8), "refrigeration method", cancellationToken).ConfigureAwait(false);
        report["concern_id"] = method.Concern;
        (int X, int Z) builtCell;
        for (var renewals = 0; ; renewals++)
        {
            var plan = await run.LoadPlanAsync(method.Plan, cancellationToken).ConfigureAwait(false);
            var actions = plan["actions"].AsSlice();
            if (actions.Count != 1)
            {
                throw new AcceptanceException($"refrigeration plan {method.Plan} has {actions.Count} actions, expected 1");
            }

            var b = RoutineService.Building(actions[0]);
            if (b is null || b["defName"].AsString() != "Cooler")
            {
                throw new AcceptanceException($"refrigeration plan action is not a Cooler build: {actions[0].ToJson()}");
            }

            builtCell = ((int)b["x"].AsNumber(), (int)b["z"].AsNumber());
            if (!walls.Contains(builtCell))
            {
                throw new AcceptanceException($"cooler placed at {builtCell}, not on a fixture wall cell");
            }

            var rotationName = b["rotation"].AsString();
            if (!DomainWire.TryParse<Rotation>(rotationName, out var rotation))
            {
                throw new AcceptanceException($"cooler at {builtCell} has an unknown rotation \"{rotationName}\"");
            }

            var placed = new RefrigerationCooler { Position = new Cell(builtCell.X, builtCell.Z), Rotation = rotation };
            var cold = placed.Cold();
            var hot = placed.Hot();
            if (!Inside(cold) || Inside(hot) || walls.Contains((hot.X, hot.Z)))
            {
                throw new AcceptanceException($"cooler at {builtCell} facing {rotationName} has cold side {cold} / hot side {hot}; expected cold inside and hot outdoors");
            }

            report["cooler_cell"] = new JsonObject { ["x"] = builtCell.X, ["z"] = builtCell.Z, ["rotation"] = rotationName };
            var planId = method.Plan;
            var (state, incidental) = await RoutineService.BoundedAsync(ct => run.Service.WaitPlanTerminalAsync(planId, ct), TimeSpan.FromMinutes(10), "refrigeration plan", cancellationToken).ConfigureAwait(false);
            if (!incidental)
            {
                report["refrigeration_plan"] = planId;
                report["refrigeration_completed_tick"] = RoutineService.CompletedTick(state);
                report["incidental_renewals"] = renewals;
                break;
            }

            method = await RoutineService.BoundedAsync(
                ct => run.Service.WaitMethodAsync(concern, planId, ct),
                TimeSpan.FromMinutes(5),
                string.Create(CultureInfo.InvariantCulture, $"renewed refrigeration method after incidental cancellation #{renewals + 1}"),
                cancellationToken).ConfigureAwait(false);
        }

        // Native cooling: the review releases its latch only when the stock's measured temperature falls to ChilledExitC,
        // and the goal is then retired. The measured food temperature is that census; the tick budget (two game days, ample
        // for a cooler against a heat wave) bounds the cooling itself.
        var policy = PolicyFunctions.DefaultFoodStoragePolicy();
        await run.WaitCensusAsync(
            "the food census never reached the refrigeration exit threshold",
            TimeSpan.FromMinutes(12),
            async ct =>
            {
                var now = await ReadFoodStorageAsync(h, identity, meat, "food-release", ct).ConfigureAwait(false);
                var tick = await run.GameTickAsync(ct).ConfigureAwait(false);
                return (Wait.Signature(now.Rows, now.WarmStacks, now.Temperature, tick), now.Rows > 0 && now.WarmStacks == 0 && now.Temperature <= policy.ChilledExitC);
            },
            cancellationToken,
            2 * Wait.TicksPerDay).ConfigureAwait(false);
        await Rounds.AssertRoundsRunningAsync(run.Service.GetAsync, cancellationToken).ConfigureAwait(false);

        // Independent native read after the service releases the game slot.
        h = await run.ReleaseAsync(s, cancellationToken).ConfigureAwait(false);
        var after = await ReadFoodStorageAsync(h, identity, meat, "food-after", cancellationToken).ConfigureAwait(false);
        report["food_after"] = after.Evidence();

        // The controller's release above is the exit-threshold evidence at its own tick. The game runs on for a few hundred
        // ticks while the service hands the slot back, so this later independent read confirms the stock is chilled (under
        // the review's entry bound) with no warm nutrition left, rather than re-applying the hysteresis exit bound.
        if (after.Rows == 0 || after.Temperature > after.ChilledMaxC || after.WarmStacks > 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"food-after: meat temperature {after.Temperature:F1} C / warm stacks {after.WarmStacks} is not chilled under {after.ChilledMaxC:F1} C (rows {after.Rows})"));
        }

        var coolers = await ReadCoolersAsync(h, identity, cancellationToken).ConfigureAwait(false);
        report["coolers_after"] = new JsonArray([.. coolers.Select(c => (JsonNode?)c.Evidence())]);
        if (coolers.Count != 1)
        {
            throw new AcceptanceException($"expected exactly 1 Cooler after the run, observed {coolers.Count}: {report["coolers_after"].ToJson()}");
        }

        var cooler = coolers[0];
        if (cooler.X != builtCell.X || cooler.Z != builtCell.Z)
        {
            throw new AcceptanceException($"cooler {cooler.Id} at ({cooler.X},{cooler.Z}) is not at the admitted cell {builtCell}");
        }

        if (cooler.Target > policy.FreezerTargetC)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"cooler {cooler.Id} target {cooler.Target:F1} C is above the freezer target {policy.FreezerTargetC:F1} C"));
        }

        try
        {
            await CheckSpoilageRecoveryAsync(s, rotId, rotBefore, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("spoilage recovery: " + e.Message, e);
        }
    }

    // The run's spoilage assertion over the seeded rotting stack: still fresh after the run (its rot advance over the run is
    // recorded, not bounded: a setpoint patch chills in a few hundred ticks, a build in a few thousand), and once it
    // measures under 0 C its CompRottable progress stops for the bulk of a measured hour.
    private static async Task CheckSpoilageRecoveryAsync(ISession s, string rotId, RotSummary before, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var after = await ReadRotAsync(s.Harness, rotId, "rot-after", cancellationToken).ConfigureAwait(false);
        report["rot_after"] = after.Evidence();
        if (after.Destroyed || after.Stage != "Fresh")
        {
            throw new AcceptanceException($"seeded stack {rotId} did not survive the run fresh: {after}");
        }

        report["rot_run_advance"] = new JsonObject { ["rot_ticks"] = after.Progress - before.Progress, ["game_ticks"] = after.Tick - before.Tick };
        var windows = 0;
        for (; after.Temperature >= 0; windows++)
        {
            if (windows == FreezeWindows)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"seeded stack still measures {after.Temperature:F1} C after {windows * FreezeWindowTicks} ticks with the cooler running: rot never stopped"));
            }

            _ = await s.AdvanceAsync(FreezeWindowTicks, null, cancellationToken).ConfigureAwait(false);
            after = await ReadRotAsync(s.Harness, rotId, "rot-freeze", cancellationToken).ConfigureAwait(false);
            if (after.Destroyed)
            {
                throw new AcceptanceException($"seeded stack {rotId} vanished while the room froze: {after}");
            }
        }

        report["rot_freeze_windows"] = windows;
        RotSummary first = after, start = after;
        var stopped = 0;
        var steps = new JsonArray();
        report["rot_window"] = new JsonObject { ["start"] = start.Evidence(), ["steps"] = steps };
        for (var step = 0; step < RotWindowSteps; step++)
        {
            _ = await s.AdvanceAsync(RotStepTicks, null, cancellationToken).ConfigureAwait(false);
            var end = await ReadRotAsync(s.Harness, rotId, "rot-window", cancellationToken).ConfigureAwait(false);
            if (end.Destroyed || end.Stage != "Fresh")
            {
                throw new AcceptanceException($"seeded stack {rotId} did not survive the frozen window fresh: {end}");
            }

            var advanced = end.Progress - start.Progress;
            steps.Add(new JsonObject { ["tick"] = end.Tick, ["temperature_c"] = end.Temperature, ["rot_advance"] = advanced, ["pawns_in_room"] = end.PawnsInRoom });
            if (advanced <= RotStoppedTolerance)
            {
                stopped++;
            }

            start = end;
        }

        var total = start.Progress - first.Progress;
        var ticks = start.Tick - first.Tick;
        report["rot_stopped_steps"] = stopped;
        report["rot_window_advance"] = new JsonObject { ["rot_ticks"] = total, ["game_ticks"] = ticks };
        if (stopped < RotStoppedSteps)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"rot progress stopped for only {stopped} of {RotWindowSteps} window steps ({total:F0} rot ticks over {ticks} game ticks)"));
        }

        if (ticks <= 0 || total > RotWindowMaxFraction * ticks)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"rot progress advanced {total:F0} over {ticks} game ticks of the frozen window, more than {100 * RotWindowMaxFraction:F0}% of the warm rate"));
        }
    }

    private sealed record RotSummary(long Tick, bool Destroyed, string Stage, double Progress, double RotTicks, double Temperature, int PawnsInRoom)
    {
        public JsonObject Evidence() => new()
        {
            ["tick"] = Tick,
            ["destroyed"] = Destroyed,
            ["stage"] = Stage,
            ["rot_progress"] = Progress,
            ["rot_ticks"] = RotTicks,
            ["temperature_c"] = Temperature,
            ["pawns_in_room"] = PawnsInRoom,
        };
    }

    // The private test/refrigeration_rot probe over one thing: its CompRottable progress, stage, runway, ambient temperature
    // and the pawns sharing its room at the game tick of the read.
    private static async Task<RotSummary> ReadRotAsync(Harness h, string id, string label, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync(label, "test/refrigeration_rot", new JsonObject { ["id"] = id }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{label}: {reply["reason"].AsString()}");
        }

        return new RotSummary(
            (long)reply["tick"].AsNumber(),
            reply["destroyed"].AsBool() == true,
            reply["stage"].AsString(),
            reply["rotProgress"].AsNumber(),
            reply["rotTicks"].AsNumber(),
            reply["temperatureC"].AsNumber(),
            (int)reply["pawnsInRoom"].AsNumber());
    }

    private sealed record FoodSummary(int Rows, double Temperature, int WarmStacks, double ChilledMaxC)
    {
        // The report-serialisable form.
        public JsonObject Evidence() => new() { ["rows"] = Rows, ["warmest_temperature_c"] = Temperature, ["warm_stacks"] = WarmStacks };
    }

    // Probes each of the fixture's meat stacks (the ids test/refrigeration_prepare returned) for ambient temperature and rot
    // runway. The colony-facts stock rows carry only item, nutrition and eaters so the fixture probe is the source of warmth;
    // the room is enclosed and roofed by construction.
    private static async Task<FoodSummary> ReadFoodStorageAsync(Harness h, JsonObject identity, IReadOnlyList<JsonNode?> meat, string label, CancellationToken cancellationToken)
    {
        var policy = PolicyFunctions.DefaultFoodStoragePolicy();
        var catalog = await h.WireAsync(
            label,
            "observations_read_definition_catalog",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        var observed = catalog["observed"].AsMap() ?? throw new AcceptanceException($"{label}: definition catalog not observed: {catalog.ToJson()}");
        var chilledMax = observed["derived"].AsMap()?["fullRotRateC"].AsNumber() ?? 0;
        var rows = 0;
        var warm = 0;
        var temperature = double.NegativeInfinity;
        foreach (var raw in meat)
        {
            var stack = await ReadRotAsync(h, raw.AsString(), label, cancellationToken).ConfigureAwait(false);
            if (stack.Destroyed)
            {
                continue;
            }

            rows++;
            if (stack.Temperature > temperature)
            {
                temperature = stack.Temperature;
            }

            if (stack.Temperature > chilledMax && stack.RotTicks > 0 && stack.RotTicks < policy.SafeRotDays * Wait.TicksPerDay)
            {
                warm++;
            }
        }

        return new FoodSummary(rows, temperature, warm, chilledMax);
    }

    private sealed record CoolerRow(string Id, int X, int Z, double Target)
    {
        public JsonObject Evidence() => new() { ["id"] = Id, ["x"] = X, ["z"] = Z, ["target_c"] = Target };
    }

    private static async Task<List<CoolerRow>> ReadCoolersAsync(Harness h, JsonObject identity, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            "coolers-after",
            "observations_list_buildings",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["defNames"] = new JsonArray("Cooler"),
                ["statuses"] = new JsonArray("built"),
            },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var coolers = new List<CoolerRow>();
        foreach (var raw in observed["buildings"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var building = row["building"].AsMap() ?? [];
            if (building["defName"].AsString() != "Cooler" || row["status"].AsString() != "BUILDING_STATUS_BUILT")
            {
                continue;
            }

            var position = building["position"].AsMap() ?? [];
            var settings = row["settings"].AsMap() ?? [];
            coolers.Add(new CoolerRow(building["id"].AsString(), (int)position["x"].AsNumber(), (int)position["z"].AsNumber(), settings["targetTemperatureC"].AsNumber()));
        }

        return coolers;
    }
}
