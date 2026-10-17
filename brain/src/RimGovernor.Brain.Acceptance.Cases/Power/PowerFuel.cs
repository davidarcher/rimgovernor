using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>power/fuel</c> (Go <c>power/power.go</c>): ordinary colonists refuel a generator while the service waits without
/// admitting generator or conduit work. An independent native read must show fuel and power at the consumer. Snapshot
/// tests cover reserve, battery, wind and geothermal decisions.
/// </summary>
/// <remarks>
/// Parity note against the Go case: the hold window's "no method committed" check reads the power/basic Need row of
/// <c>/api/routines</c> (a goal's committed method is its <c>method</c> member) instead of the journal's goal record.
/// </remarks>
internal static class PowerFuel
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "power/fuel",
        Scope = "Native power/basic reliability vertical on the lab: an out-of-fuel generator holds the live Go power family "
            + "until native colonists refuel it, confirmed by an independent native read.",
        Start = new FixtureStart { Op = "test/power_prepare", Args = new JsonObject { ["scenario"] = "fuel" }, On = new LabStart() },
        Service = true,

        // ~2x the measured healthy run: the refuel hold plays ~4 minutes.
        Budget = TimeSpan.FromMinutes(10),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        var generatorId = prepared["generator"].AsString();
        var ids = new List<string>();
        if (generatorId.Length > 0)
        {
            ids.Add(generatorId);
        }

        var consumers = prepared["consumers"].AsSlice().Select(raw => raw.AsString()).ToList();
        ids.AddRange(consumers);

        Task<PowerRead> Observe(Harness on, string label) => PowerKit.ObserveAsync(on, identity, ids, label, cancellationToken);

        var before = await Observe(h, "power-before").ConfigureAwait(false);
        report["power_before"] = before.ToReport();
        if (before.Rows.GetValueOrDefault(generatorId)?.Service?.OutOfFuel != true)
        {
            throw new AcceptanceException($"power-before: fixture generator {generatorId} is not out of fuel");
        }

        foreach (var id in consumers)
        {
            if (before.Rows.GetValueOrDefault(id)?.Service?.PowerOn == true)
            {
                throw new AcceptanceException($"power-before: consumer {id} already powered");
            }
        }

        // The typed colony facts the family reads must carry the fuel and network facts.
        report["colony_power_before"] = await ReadPowerFactsAsync(h, identity, "colony-facts-before", cancellationToken).ConfigureAwait(false);

        var run = await RoutineService.StartAsync(s, PowerKit.Prefix,["power", "work"], cancellationToken).ConfigureAwait(false);
        try
        {
            // Hold for a bounded window of Fast-speed simulation: the power goal may bind (the consumer is unpowered) but
            // no method may be committed while the generator merely wants refuelling.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(4);
            var sawStandard = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var routines = await run.Service.GetAsync("GET", "/api/routines", cancellationToken).ConfigureAwait(false);
                if (RoutineService.ConcernRow(routines, PowerFamily.Basic.Value) is { } row)
                {
                    sawStandard = true;
                    if (row["method"].AsString().Length > 0)
                    {
                        throw new AcceptanceException($"power family committed method {row["method"].AsString()} while the generator was only out of fuel: {row.ToJson()}");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }

            report["power_standard_bound"] = sawStandard;
            await Rounds.AssertRoundsRunningAsync(run.Service.GetAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await RoutineService.PostmortemAsync(s, run, (_, _) => Task.CompletedTask).ConfigureAwait(false);
            throw;
        }

        h = await run.ReleaseAsync(s, cancellationToken).ConfigureAwait(false);
        var after = await Observe(h, "power-after").ConfigureAwait(false);
        report["power_after"] = after.ToReport();
        if (after.Rows.GetValueOrDefault(generatorId)?.Service?.OutOfFuel == true)
        {
            throw new AcceptanceException($"power-after: generator {generatorId} still out of fuel after the hold window; colonists never refuelled it");
        }

        // A refuel landing just before the pause leaves the generator fuelled while the power net has not ticked its
        // consumers back on; let the game run a few seconds and read again before judging.
        for (var attempt = 1; attempt <= 3 && !PowerKit.AllPowered(after, consumers); attempt++)
        {
            var label = string.Create(CultureInfo.InvariantCulture, $"after-{attempt}");
            await PowerKit.RunBrieflyAsync(h, label, cancellationToken).ConfigureAwait(false);
            after = await Observe(h, "power-" + label).ConfigureAwait(false);
            report["power_after"] = after.ToReport();
        }

        foreach (var id in consumers)
        {
            if (after.Rows.GetValueOrDefault(id)?.Service?.PowerOn != true)
            {
                throw new AcceptanceException($"power-after: consumer {id} still unpowered after refuelling");
            }
        }

        if (after.Generators.Count != 1)
        {
            throw new AcceptanceException($"power-after: expected the single fixture generator, observed {after.Generators.Count}: [{string.Join(' ', after.Generators.Select(PowerKit.Id))}]");
        }

        report["colony_power_after"] = await ReadPowerFactsAsync(h, identity, "colony-facts-after", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the typed development power rows, network summaries and steam geysers the power family decodes, so the
    /// report carries the exact fuel/battery/network facts the decision was made on.
    /// </summary>
    private static async Task<JsonObject> ReadPowerFactsAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label,
            "observations_read_colony_facts",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() }, ["planning"] = false },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        JsonObject development;
        try
        {
            development = Harness.Outcome(observed["development"].AsMap() ?? [], "observed").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: development section unavailable: {e.Message}", e);
        }

        var power = development["power"].AsSlice();
        if (power.Count == 0)
        {
            throw new AcceptanceException($"{label}: no development power rows observed");
        }

        var rows = await h.BuildingRowsAsync(label + "-buildings", identity, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < power.Count; i++)
        {
            var row = power[i].AsMap() ?? [];
            var entity = row["building"].AsMap() ?? [];
            var id = entity["id"].AsString();
            if (!rows.TryGetValue(id, out var building))
            {
                throw new AcceptanceException($"{label}: power row {i} ({id}) is not in the building table");
            }

            var service = building["service"].AsMap() ?? [];
            if (!service.ContainsKey("outOfFuel") && entity["defName"].AsString() == "WoodFiredGenerator")
            {
                throw new AcceptanceException($"{label}: power row {i} (WoodFiredGenerator) lacks the refuelable service facts");
            }
        }

        return new JsonObject
        {
            ["power"] = new JsonArray([.. power.Select(p => p?.DeepClone())]),
            ["networks"] = development["networks"]?.DeepClone(),
            ["geysers"] = development["geysers"]?.DeepClone(),
        };
    }
}
