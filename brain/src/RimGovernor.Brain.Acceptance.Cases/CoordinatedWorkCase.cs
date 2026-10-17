using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class CoordinatedWorkCase
{
    internal static AcceptanceCase Case(string mode, bool interrupted = false) => new()
    {
        Name = "coordination/" + mode + (interrupted ? "-carried-recovery" : "-delegated"),
        Tier = CaseTier.Nightly,
        Scope = "Prepared specialist/helper facts enter ordinary Rounds generation, shared comparison, Method admission and Hands. "
            + "Observe a multi-participant task graph, physical " + (mode == "food" ? "harvested food at selected storage" : "material delivery and a finished building")
            + (interrupted ? ", including one interrupted carried input and a later exact revision" : "")
            + ", followed by participant release. Unit tests cannot prove native carry conservation, blueprint/frame rebinding or actual completion. The fixture installs no selected tasks.",
        Start = new FixtureStart { Op = "test/coordinated_work_prepare", On = new LabStart(),
            Args = new JsonObject { ["mode"] = mode, ["interrupt"] = interrupted } },
        RequiredOps = ["test/coordinated_work_prepare", "test/coordinated_work_read"],
        QuietWorld = true, Crew = new Crew { Size = 3 }, Service = true, NoCheckpoint = true,
        Budget = TimeSpan.FromMinutes(4), Stall = TimeSpan.FromSeconds(45),
        Run = async (s, ct) =>
        {
            var run = await BrainRun.StartAsync(s, null, "coordination", "coordination", ct).ConfigureAwait(false);
            try
            {
                var wait = new Wait { Stall = TimeSpan.FromSeconds(45), Terminal = run.Service.Exited };
                await wait.WaitProgressAsync(async token =>
                {
                    var read = await s.Harness.CallAsync("coordination-observe", "test/coordinated_work_read", [], token).ConfigureAwait(false);
                    s.Report["coordination"] = read;
                    if (read["forced"].Flag()) throw new AcceptanceException("Planner work became player-forced: " + read.ToJson());
                    var physical = read[mode == "food" ? "physicalFood" : "physicalBuildings"].AsNumber() > 0;
                    var kinds = read["finishedKinds"].AsSlice().Select(v => v.AsString()).ToArray();
                    var chain = mode == "food" ? kinds.Contains("Work:Harvest") && kinds.Contains("Deliver:")
                        : kinds.Contains("Supply:") && kinds.Contains("Work:FinishFrame");
                    var complete = read["joint"].Flag() && chain && physical && read["delivered"].AsNumber() > 0
                        && read["active"].AsNumber() == 0 && read["participants"].AsSlice().Count > 0
                        && read["released"].AsSlice().Count == read["participants"].AsSlice().Count
                        && (!interrupted || read["interrupted"].Flag() && read["recovered"].Flag());
                    if (!complete && read["elapsed"].AsNumber() >= 6000)
                        throw new AcceptanceException("Selected coordination chain did not physically finish within 6000 ticks: " + read.ToJson());
                    return (Wait.Signature(read["tick"].AsNumber()), complete);
                }, ct).ConfigureAwait(false);
            }
            finally { await run.StopAsync(ct).ConfigureAwait(false); }
        }
    };
}
