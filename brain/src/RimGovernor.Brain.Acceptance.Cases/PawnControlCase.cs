using System.Text.Json.Nodes;
namespace RimGovernor.Brain.Acceptance.Cases;
internal static class PawnControlCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "control/pawn-activities",
        Scope = "Planner-owned selection bypasses hunger/rest arbitration and native overrides while physical cleaning, harvest, ingestion and sleep execute. Snapshots cannot prove the native selection boundary.",
        Start = new FixtureStart { Op = "test/pawn_control_prepare", On = new LabStart() },
        RequiredOps = ["test/pawn_control_prepare", "test/pawn_control_install", "test/pawn_control_read", "test/pawn_control_release", "test/pawn_control_loaded"],
        Keep = ["Food", "Rest"], QuietWorld = true, Crew = new Crew { Size = 3 }, Budget = TimeSpan.FromMinutes(4), Run = RunAsync,
    };
    internal static AcceptanceCase LifecycleCase() => Case() with
    {
        Name = "control/pawn-activity-lifecycle",
        Scope = "Prepared native probes verify atomic batch refusal, non-resetting hold deadlines, native job refusal and player authority takeover.",
        RequiredOps = ["test/pawn_control_prepare", "test/pawn_control_install", "test/pawn_control_lifecycle"],
        Run = async (s, ct) =>
        {
            _ = await s.RuntimeAsync(ct).ConfigureAwait(false);
            _ = await s.Harness.CallAsync("install", "test/pawn_control_install", [], ct).ConfigureAwait(false);
            s.Report["lifecycle"] = await s.Harness.CallAsync("lifecycle", "test/pawn_control_lifecycle", [], ct).ConfigureAwait(false);
        },
    };
    private static async Task RunAsync(ISession s, CancellationToken ct)
    {
        _ = await s.RuntimeAsync(ct).ConfigureAwait(false);
        _ = await s.Harness.CallAsync("install", "test/pawn_control_install", [], ct).ConfigureAwait(false);
        var held = false;
        for (var ticks = 0; ticks < 6000; ticks += 30)
        {
            _ = await s.AdvanceAsync(30, null, ct).ConfigureAwait(false);
            var state = await s.Harness.CallAsync("activity-" + ticks, "test/pawn_control_read", [], ct).ConfigureAwait(false);
            if (state["worker"]?["state"].AsString() == "failed") throw new AcceptanceException(state.ToJson());
            if (state["filthGone"].AsBool() == false)
            {
                held = true;
                if (state["job"].AsString() != "RimGovernorWait") throw new AcceptanceException("Producer escaped the dependency hold: " + state.ToJson());
            }
            if (state["forced"].AsBool() == true) throw new AcceptanceException("Activity became player-forced.");
            if (state["job"].AsString() == "LayDown" && state["food"].AsNumber() > .3 && state["rest"].AsNumber() > .12)
            {
                if (!held || state["filthGone"].AsBool() != true || state["cropHarvested"].AsBool() != true) throw new AcceptanceException("Missing physical support/production evidence.");
                s.Report["activity"] = state.DeepClone();
                var name = "pawn-control-" + Guid.NewGuid().ToString("N");
                var saved = await s.Harness.WireAsync("save", "lifecycle_save", new JsonObject
                {
                    ["player"] = new JsonObject { ["identity"] = s.Identity.DeepClone(), ["playerDirection"] = 1, ["requestId"] = name }, ["saveName"] = name,
                }, ct).ConfigureAwait(false);
                _ = Harness.Outcome(saved, "completed");
                var loaded = await s.Harness.WireAsync("load", "lifecycle_load", new JsonObject
                {
                    ["requestId"] = name + "-load", ["saveName"] = name, ["readiness"] = "READINESS_MAP", ["playerDirection"] = 1,
                    ["expectedPlayer"] = new JsonObject { ["identity"] = s.Identity.DeepClone(), ["playerDirection"] = 1, ["requestId"] = name + "-load" },
                }, ct).ConfigureAwait(false);
                _ = await GameReuse.PollLoadAsync(s.Harness, loaded, name + "-load", "load-poll", ct).ConfigureAwait(false);
                _ = await s.Harness.CallAsync("loaded", "test/pawn_control_loaded", [], ct).ConfigureAwait(false);
                return;
            }
        }
        throw new AcceptanceException("No completed support/work/food/sleep sequence within the bounded fixture.");
    }
}