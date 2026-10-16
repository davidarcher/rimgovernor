using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class PortfolioThermalCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "production/coupled-thermal",
        Scope = "A brief normal review supplies its own room geometry. A prepared powered but leaky cold room then enters normal portfolio selection. Require journaled roof work, actual closure and warmer occupied sleep; no chosen task or Method is installed. Unit tests cannot prove native roof jobs, temperature or occupied use.",
        Start = new LabStart(),
        RequiredOps = ["test/portfolio_thermal_prepare", "test/portfolio_thermal_read"],
        Keep = ["Food", "Rest", "Joy"], QuietWorld = true, Crew = new Crew { Size = 3 },
        Service = true, NoCheckpoint = true, Budget = TimeSpan.FromMinutes(15), Stall = TimeSpan.FromSeconds(120),
        Run = async (s, ct) =>
        {
            LayoutPlan? layout = null;
            var initial = await BrainRun.StartAsync(s, null, "thermal-layout", "thermal-layout", ct).ConfigureAwait(false);
            try
            {
                await RoutineService.BoundedAsync(async token =>
                {
                    while (true)
                    {
                        (_, layout) = await ObservedSession.ReadLayoutPlanAsync(s.Config, token).ConfigureAwait(false);
                        if (layout is not null && layout.Rooms.Count > 0) return true;
                        await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
                    }
                }, TimeSpan.FromSeconds(30), "first normal layout review", ct).ConfigureAwait(false);
            }
            finally { await initial.StopAsync(ct).ConfigureAwait(false); }
            s.Report["plannedRooms"] = new JsonArray(layout!.Rooms.OrderBy(r => r.Id).Select(r =>
            {
                var b = r.Region.Bounds();
                return (JsonNode)new JsonObject { ["id"] = r.Id, ["role"] = r.Role.ToString(), ["outdoor"] = r.Outdoor,
                    ["x"] = b.X, ["z"] = b.Z, ["width"] = b.Width, ["height"] = b.Height,
                    ["cells"] = r.Region.Cells().Count, ["doorX"] = r.Door.X, ["doorZ"] = r.Door.Z };
            }).ToArray());
            var room = layout!.Rooms.Where(r => !r.Outdoor).OrderBy(r => r.Id).Take(8)
                .Where(r => r.Role == PolicyFunctions.PlannedBedroom || r.Role == PolicyFunctions.PlannedShelter)
                .Where(r => r.Region.Bounds() is var b && b.Width >= 2 && b.Height >= 2 && b.Width * b.Height >= 6 && b.Width <= 5 && b.Height <= 5
                    && r.Region.Cells().Count == b.Width * b.Height)
                .OrderBy(r => r.Region.Cells().Count).ThenBy(r => r.Id).FirstOrDefault()
                ?? throw new AcceptanceException("Normal first review supplied no supported small rectangular sleeping room; thermal setup cannot invent planner geometry.");
            var bounds = room.Region.Bounds();
            var prepared = await s.Harness.CallAsync("thermal-stage", "test/portfolio_thermal_prepare", new JsonObject
            {
                ["x"] = bounds.X, ["z"] = bounds.Z, ["width"] = bounds.Width, ["height"] = bounds.Height,
                ["doorX"] = room.Door.X, ["doorZ"] = room.Door.Z
            }, ct).ConfigureAwait(false);
            s.Report["prepared"] = prepared;
            var run = await BrainRun.StartAsync(s, null, "coupled-thermal", "coupled-thermal", ct).ConfigureAwait(false);
            try
            {
                using var journal = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), ct).ConfigureAwait(false);
                var witnessed = new HashSet<string>(StringComparer.Ordinal);
                var wait = new Wait { Stall = TimeSpan.FromSeconds(120), Terminal = run.Service.Exited };
                await wait.WaitProgressAsync(async token =>
                {
                    var read = await s.Harness.CallAsync("thermal-observe", "test/portfolio_thermal_read", [], token).ConfigureAwait(false);
                    foreach (var owner in await journal.OpenStandardsAsync(PolicyFunctions.EnsureWorkAssignments, token).ConfigureAwait(false))
                        foreach (var method in await journal.LoadMethodsAsync(owner.Standard.Id, owner.Standard.Episode, token).ConfigureAwait(false))
                            witnessed.Add(method.MethodId.Value);
                    var applied = read["methods"]!.AsArray().Select(n => n.AsString()).ToArray();
                    var journaled = applied.Length > 0 && applied.All(witnessed.Contains);
                    s.Report["thermal"] = read; s.Report["journaledRoofWork"] = journaled;
                    var complete = journaled && read["roofComplete"].Flag() && read["openRoof"].AsNumber() == 0
                        && read["powered"].Flag() && read["slept"].Flag()
                        && read["occupiedPeak"].AsNumber() >= read["baseline"].AsNumber() + 3;
                    if (!complete && read["elapsed"].AsNumber() >= 45000)
                        throw new AcceptanceException("Normal thermal portfolio did not deliver observed roof closure and occupied warming: " + read.ToJson());
                    return (Wait.Signature(read["tick"].AsNumber()), complete);
                }, ct).ConfigureAwait(false);
            }
            finally { await run.StopAsync(ct).ConfigureAwait(false); }
        }
    };
}
