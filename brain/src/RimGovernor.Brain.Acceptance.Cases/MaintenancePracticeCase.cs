using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
namespace RimGovernor.Brain.Acceptance.Cases;

internal static class MaintenancePracticeCase
{
    internal static AcceptanceCase Case(bool melee) => new()
    {
        Name = "production/maintenance-" + (melee ? "sparring" : "range"),
        Scope = "An actual assigned Ideology combat requirement below its native floor enters ordinary shared selection, Method journal and Hands. Observe native skill recovery, selected participant ownership, draft interruption with original gear restoration, and release after removing the role. Unit tests cannot prove native practice, gear restoration or competing-writer cutover.",
        Start = new FixtureStart { Op = "test/maintenance_prepare", Args = new JsonObject { ["melee"] = melee }, On = new LabStart() },
        RequiredOps = ["test/maintenance_prepare", "test/maintenance_read", "test/maintenance_disturb"],
        QuietWorld = true, Crew = new Crew { Size = 3 }, Service = true, NoCheckpoint = true,
        Budget = TimeSpan.FromMinutes(5), Stall = TimeSpan.FromSeconds(45),
        Run = async (s, ct) =>
        {
            var run = await BrainRun.StartAsync(s, null, "maintenance", "maintenance", ct).ConfigureAwait(false);
            try
            {
                using var store = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), ct).ConfigureAwait(false);
                var journaled = new HashSet<string>(StringComparer.Ordinal);
                var stage = 0; var releasedAt = 0d;
                await new Wait { Stall = TimeSpan.FromSeconds(45), Terminal = run.Service.Exited }.WaitProgressAsync(async token =>
                {
                    var read = await s.Harness.CallAsync("maintenance", "test/maintenance_read", [], token).ConfigureAwait(false);
                    s.Report["observed"] = read;
                    foreach (var owner in await store.OpenStandardsAsync(PolicyFunctions.EnsureWorkAssignments, token).ConfigureAwait(false))
                        foreach (var method in await store.LoadMethodsAsync(owner.Standard.Id, owner.Standard.Episode, token).ConfigureAwait(false)) journaled.Add(method.MethodId.Value);
                    var methods = read["methods"]!.AsArray().Select(n => n.AsString()).ToArray();
                    var normal = methods.Length > 0 && methods.All(journaled.Contains);
                    if (stage == 0 && normal && read["earned"].Flag() && read["activity"].AsString() != "running")
                    {
                        await s.Harness.CallAsync("maintenance-interrupt", "test/maintenance_disturb", new JsonObject { ["action"] = "interrupt" }, token).ConfigureAwait(false);
                        stage = 1;
                    }
                    if (stage == 1 && read["drafted"].Flag() && read["restored"].Flag())
                    {
                        await s.Harness.CallAsync("maintenance-release", "test/maintenance_disturb", new JsonObject { ["action"] = "release" }, token).ConfigureAwait(false);
                        stage = 2; releasedAt = read["tick"].AsNumber();
                    }
                    var complete = stage == 2 && read["released"].Flag() && read["restored"].Flag()
                        && read["tick"].AsNumber() >= releasedAt + 500 && read["job"].AsString() is not ("RimGovernor_Spar" or "RimGovernor_TrainShooting");
                    if (!complete && read["elapsed"].AsNumber() >= 18000) throw new AcceptanceException("Maintenance did not recover, interrupt and release within 18000 ticks: " + read.ToJson());
                    return (Wait.Signature(read["tick"].AsNumber(), stage), complete);
                }, ct).ConfigureAwait(false);
            }
            finally { await run.StopAsync(ct).ConfigureAwait(false); }
        }
    };
}
