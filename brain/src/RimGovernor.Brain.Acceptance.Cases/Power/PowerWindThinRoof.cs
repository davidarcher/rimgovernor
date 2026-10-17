using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using DomainWire = RimGovernor.Brain.Domain.Wire;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>power/wind-thin-roof</c> (Go <c>power/wind_thin_roof.go</c>): the power/basic Need on a lab that is solid granite under
/// thin mountain roof except a 30x30 pocket holding one unpowered lamp. The layout plan's turbine pair stands on rock
/// beside the pocket, so the one plan the family admits digs the rock of the turbine's wind path and removes the roof
/// over it in dig waves holding no building; the ordinary plan then places a WindTurbine once the site reads clear, and
/// a follow-up connects it. Confirmed natively: the turbine stands on a planned site with no roofed or wind-blocking cell
/// on its wind path and is on the lamp's network.
/// </summary>
/// <remarks>
/// Parity notes against the Go case: a plan is read through <c>/api/plan</c>, whose actions name their kind but not the
/// roof cells a remove-roof action covers, so the wave records <c>roof_actions</c> instead of <c>roof_cells</c>. The
/// planned turbine sites come from the layout plan the journal recorded (<see cref="ObservedSession.ReadLayoutPlanAsync"/>).
/// </remarks>
internal static class PowerWindThinRoof
{
    // Bounds the connect follow-ups. The family lays at most 8 conduit cells per plan (connectLiveBeforeUpgrade), and the
    // lab's lamp lies about 42 route cells from the turbine site, so the connection takes six plans (a bound of four
    // stopped the case 5 cells short of the lamp).
    private const int FollowUpPlanBound = 8;

    internal static AcceptanceCase Case() => new()
    {
        Name = "power/wind-thin-roof",
        Scope = "Issue #1873: on a lab of granite under thin mountain roof with one unpowered lamp in a pocket, the power family "
            + "digs and unroofs the layout's turbine site and wind path in building-free waves, places a WindTurbine on the planned site, "
            + "and connects it; an independent native read finds no roofed or wind-blocking cell on its wind path and the lamp "
            + "powered while the wind blows. Native: the dig, roof removal and over-rock preview are native operations and the "
            + "roof and wind are vanilla physics, so a snapshot test over recorded facts cannot cover it.",
        Start = new FixtureStart { Op = "test/power_prepare", Args = new JsonObject { ["scenario"] = "mountain" }, On = new LabStart() },
        Service = true,
        Budget = TimeSpan.FromMinutes(30),
        Crew = new Crew { Size = 3 },
        Reason = "three colonists mine, unroof and build a 7x16 turbine path out of solid granite",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var prepared = s.Prepared ?? [];
        var rock = prepared["rockCells"].AsNumber();
        if (rock < 5000)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"fixture raised only {rock} rock cells: {prepared.ToJson()}"));
        }

        var consumers = prepared["consumers"].AsSlice().Select(raw => raw.AsString()).ToList();
        var ids = new List<string>(consumers);
        var before = await PowerKit.ObserveAsync(h, s.Identity, ids, "power-before", cancellationToken).ConfigureAwait(false);
        report["power_before"] = before.ToReport();
        if (before.Generators.Count != 0)
        {
            throw new AcceptanceException($"power-before: the mountain fixture spawned {before.Generators.Count} generators: [{string.Join(' ', before.Generators.Select(PowerKit.Id))}]");
        }

        foreach (var id in consumers)
        {
            if (before.Rows.GetValueOrDefault(id)?.Service?.PowerOn == true)
            {
                throw new AcceptanceException($"power-before: consumer {id} powered with no generator");
            }
        }

        var run = await RoutineService.StartAsync(s, PowerKit.Prefix, ["power", "work"], cancellationToken).ConfigureAwait(false);
        var concern = PowerFamily.Basic.Value;
        var method = await RoutineService.BoundedAsync(ct => run.Service.WaitMethodAsync(concern, null, ct), TimeSpan.FromMinutes(8), "first power method", cancellationToken).ConfigureAwait(false);

        report["concern_id"] = method.Concern;
        var sites = await PlannedTurbinesAsync(s, cancellationToken).ConfigureAwait(false);
        report["planned_turbines"] = new JsonArray([.. sites.Select(site => (JsonNode?)site)]);

        // The sky dig and unroof waves come first, each its own plan holding no building; the turbine is the ordinary
        // plan placed once the site reads clear. Walk the plans until the turbine's settles.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int waves = 0, renewals = 0;
        bool sawDig = false, sawRoof = false;
        for (var turbinePlaced = false; !turbinePlaced;)
        {
            var plan = await run.LoadPlanAsync(method.Plan, cancellationToken).ConfigureAwait(false);
            int digs, roofs;
            bool turbine;
            try
            {
                (digs, roofs, turbine) = CheckSkyPlan(plan["actions"].AsSlice());
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"power plan {method.Plan}: {e.Message}", e);
            }

            sawDig |= digs > 0;
            sawRoof |= roofs > 0;
            var planId = method.Plan;
            var (_, incidental) = await RoutineService.BoundedAsync(ct => run.Service.WaitPlanTerminalAsync(planId, ct), TimeSpan.FromMinutes(20), $"power plan {planId}", cancellationToken).ConfigureAwait(false);

            seen.Add(method.Plan);
            if (incidental)
            {
                renewals++;
                method = await NextMethodAsync(run, concern, seen, TimeSpan.FromMinutes(5), $"renewed power method after incidental cancellation #{renewals}", cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (turbine)
            {
                turbinePlaced = true;
                continue;
            }

            waves++;
            report[string.Create(CultureInfo.InvariantCulture, $"dig_wave_{waves}")] = new JsonObject { ["plan"] = method.Plan, ["excavations"] = digs, ["roof_actions"] = roofs };
            method = await NextMethodAsync(run, concern, seen, TimeSpan.FromMinutes(5), $"power method after dig wave {waves}", cancellationToken).ConfigureAwait(false);
        }

        if (!sawDig || !sawRoof)
        {
            throw new AcceptanceException($"the turbine was placed without digging ({sawDig}) and unroofing ({sawRoof}) its site first");
        }

        report["dig_waves"] = waves;
        report["incidental_renewals"] = renewals;
        report["turbine_plan"] = method.Plan;
        seen.Add(method.Plan);
        await FollowUpsAsync(run, concern, seen, report, cancellationToken).ConfigureAwait(false);
        await Rounds.AssertRoundsRunningAsync(run.Service.GetAsync, cancellationToken).ConfigureAwait(false);

        h = await run.ReleaseAsync(s, cancellationToken).ConfigureAwait(false);
        var after = await PowerKit.ObserveAsync(h, s.Identity, ids, "power-after", cancellationToken).ConfigureAwait(false);
        report["power_after"] = after.ToReport();
        if (after.Generators.Count != 1)
        {
            throw new AcceptanceException($"power-after: expected exactly the turbine the controller built, observed {after.Generators.Count}: [{string.Join(' ', after.Generators.Select(PowerKit.Id))}]");
        }

        var built = after.Generators[0];
        var builtId = PowerKit.Id(built);
        ids.Add(builtId);
        var definition = built.Building?.DefName;
        if (definition != PolicyConstants.WindTurbineDefinition)
        {
            throw new AcceptanceException($"power-after: the controller's generator {builtId} is a {definition}, expected {PolicyConstants.WindTurbineDefinition}");
        }

        if (built.Service?.Connected != true)
        {
            throw new AcceptanceException($"power-after: the turbine {builtId} is not connected to a power net");
        }

        var position = TypedReads.CellOf(built.Building?.Position, builtId);
        var at = string.Create(CultureInfo.InvariantCulture, $"{position.X},{position.Z}");
        if (!sites.Contains(at))
        {
            throw new AcceptanceException($"power-after: the turbine at ({at}) is on none of the planned sites [{string.Join(' ', sites)}]");
        }

        // The wind path cells vanilla counts as blocking: off the map, roofed or holding a wind-blocking thing.
        if (built.Service?.WindPathBlockedCells is not 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"power-after: the turbine's wind path has {built.Service?.WindPathBlockedCells} blocked cells"));
        }

        var footprint = TypedReads.CellsOf(built.Occupied, builtId);
        var footprintCells = await h.BoundingCellsAsync("turbine-footprint", s.Identity, footprint, cancellationToken).ConfigureAwait(false);
        if (footprint.All(c => footprintCells.At(c)?.Roofed == true))
        {
            throw new AcceptanceException($"power-after: the turbine's footprint is still roofed");
        }

        // A turbine powers the lamp only while the wind blows: read again while the game runs a few seconds at a time,
        // and accept a calm read.
        after = await PowerKit.ObserveAsync(h, s.Identity, ids, "power-after-built", cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; attempt <= 12 && !PowerKit.AllPowered(after, consumers); attempt++)
        {
            var label = string.Create(CultureInfo.InvariantCulture, $"after-{attempt}");
            await PowerKit.RunBrieflyAsync(h, label, cancellationToken).ConfigureAwait(false);
            after = await PowerKit.ObserveAsync(h, s.Identity, ids, "power-" + label, cancellationToken).ConfigureAwait(false);
            report["power_after"] = after.ToReport();
        }

        var output = after.Rows.GetValueOrDefault(builtId)?.Service?.PowerOutputW ?? 0;
        if (output == 0)
        {
            report["wind_calm_at_read"] = true;
        }
        else
        {
            foreach (var id in consumers)
            {
                if (after.Rows.GetValueOrDefault(id)?.Service?.PowerOn != true)
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"power-after: consumer {id} unpowered while the turbine outputs {output}W"));
                }
            }
        }
    }

    // The next committed method of the goal, other than the plans already followed.
    private static async Task<ServiceMethod> NextMethodAsync(RoutineService run, string concern, IReadOnlySet<string> seen, TimeSpan ceiling, string what, CancellationToken cancellationToken)
    {
        return await RoutineService.BoundedAsync(ct => run.Service.WaitMethodExcludingAsync(concern, seen, ct), ceiling, what, cancellationToken).ConfigureAwait(false);
    }

    // Waits for the layout plan the first review recorded and returns its wind turbine sites as "x,z" centres.
    private static async Task<List<string>> PlannedTurbinesAsync(ISession s, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
        while (true)
        {
            var (_, layout) = await ObservedSession.ReadLayoutPlanAsync(s.Config, cancellationToken).ConfigureAwait(false);
            if (layout is not null)
            {
                var sites = PolicyFunctions.PlannedPowerSites(layout, PolicyConstants.WindTurbineDefinition)
                    .Select(site => string.Create(CultureInfo.InvariantCulture, $"{site.Cell.X},{site.Cell.Z}"))
                    .ToList();
                return sites.Count == 0 ? throw new AcceptanceException("the layout plan reserves no wind turbine site") : sites;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new AcceptanceException("no layout plan recorded within 3 minutes of the first power method");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    // Classifies a plan of the sky method: a dig wave of excavations and a roof removal, or the one WindTurbine build alone.
    private static (int Excavations, int RoofActions, bool Turbine) CheckSkyPlan(IReadOnlyList<JsonNode?> actions)
    {
        int excavations = 0, roofs = 0, turbines = 0;
        foreach (var a in actions)
        {
            var kind = a.AsMap()?["kind"].AsString();
            if (kind == DomainWire.Name(ActionKind.Excavation))
            {
                excavations++;
                continue;
            }

            if (kind == DomainWire.Name(ActionKind.RemoveRoof))
            {
                roofs++;
                continue;
            }

            var b = RoutineService.Building(a);
            if (b is null || b["defName"].AsString() != PolicyConstants.WindTurbineDefinition)
            {
                throw new AcceptanceException($"unexpected action in the sky plan: {a.ToJson()}");
            }

            turbines++;
        }

        if (turbines > 1 || (turbines == 1 && (excavations > 0 || roofs > 0)))
        {
            throw new AcceptanceException($"a turbine plan holds {turbines} turbines, {excavations} digs, {roofs} roof actions: the dig and the build are separate plans");
        }

        if (turbines == 0 && excavations == 0 && roofs == 0)
        {
            throw new AcceptanceException("an empty plan");
        }

        return (excavations, roofs, turbines == 1);
    }

    // Lets the family connect the new turbine: a bounded number of conduit (or bank) plans, none of which may raise
    // another generator.
    private static async Task FollowUpsAsync(RoutineService run, string concern, HashSet<string> seen, Report report, CancellationToken cancellationToken)
    {
        for (var n = 1; n <= FollowUpPlanBound; n++)
        {
            ServiceMethod next;
            using (var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                bound.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    next = await run.Service.WaitMethodExcludingAsync(concern, seen, bound.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested && (e is OperationCanceledException || e.InnerException is WaitError))
                {
                    // A satisfied goal admits nothing more: the bounded wait running out or the service going quiet ends
                    // the follow-ups.
                    report["follow_up_wait_ended"] = e.Message;
                    return;
                }
            }

            seen.Add(next.Plan);
            var plan = await run.LoadPlanAsync(next.Plan, cancellationToken).ConfigureAwait(false);
            var actions = plan["actions"].AsSlice();
            foreach (var action in actions)
            {
                var b = RoutineService.Building(action);
                var definition = b?["defName"].AsString();
                if (b is null || (definition != "HiddenConduit" && definition != PolicyConstants.BatteryDefinition))
                {
                    throw new AcceptanceException($"power family committed an unexpected action after the turbine: {action.ToJson()}");
                }
            }

            var (state, incidental) = await RoutineService.BoundedAsync(ct => run.Service.WaitPlanTerminalAsync(next.Plan, ct), TimeSpan.FromMinutes(5), $"follow-up power plan {next.Plan}", cancellationToken).ConfigureAwait(false);

            report[string.Create(CultureInfo.InvariantCulture, $"follow_up_plan_{n}")] = new JsonObject
            {
                ["plan"] = next.Plan,
                ["actions"] = actions.Count,
                ["incidental"] = incidental,
                ["completed_tick"] = RoutineService.CompletedTick(state),
            };
        }
    }
}
