using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class NeedsForecastCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "observations/needs-forecast",
        Scope = "Prepared meal, bed and horseshoes scenes compare current native need rates with observed level changes and #3125 activity intervals. Records residuals and validity limits; does not certify calibrated break risk or autonomous needs selection. Pure tests cannot establish native recovery/toil timing.",
        Start = new FixtureStart { Op = "test/pawn_control_prepare", On = new LabStart() },
        RequiredOps = ["test/pawn_control_prepare", "test/needs_forecast_select", "test/needs_forecast_read"],
        Keep = ["Food", "Rest", "Joy"], QuietWorld = true, Crew = new Crew { Size = 3 }, Budget = TimeSpan.FromMinutes(4),
        Run = async (s, ct) =>
        {
            _ = await s.RuntimeAsync(ct).ConfigureAwait(false);
            for (var stage = 1; stage <= 3; stage++)
            {
                var before = await s.Harness.CallAsync("needs-select-" + stage, "test/needs_forecast_select", new() { ["stage"] = stage }, ct).ConfigureAwait(false);
                JsonObject after = new();
                for (var tick = 0; tick < 1200; tick += 30)
                {
                    _ = await s.AdvanceAsync(30, null, ct).ConfigureAwait(false);
                    after = await s.Harness.CallAsync($"needs-{stage}-{tick}", "test/needs_forecast_read", [], ct).ConfigureAwait(false);
                    if (after["execution"]?["State"].AsString() == "failed")
                        throw new AcceptanceException("Prepared needs execution failed: " + after.ToJson());
                }
                var logs = Path.Combine(GameSessions.EndpointDirectory(s.Config.Configuration, s.Config.GameId), "logs.jsonl");
                var from = (long)before["start"].AsNumber(); var to = (long)after["tick"].AsNumber();
                var intervals = PawnActivityReport.Read(logs, before["world"].AsString(), before["pawn"].AsString(), from, to);
                var service = intervals.Where(r => !r.Gap && r.Activity == "needs").Sum(r => r.End - r.Start);
                if (service <= 0) throw new AcceptanceException("No observed need service interval.");
                var kind = before["kind"].AsString();
                var observed = after[kind].AsNumber();
                var initial = before["initial"].AsNumber(); var fall = before["fall"].AsNumber(); var gain = before["gain"].AsNumber();
                var predicted = Math.Clamp(initial + (stage == 3 ? gain : service * gain) - (to - from - (stage == 3 ? 0 : service)) * fall, 0, 1);
                if (observed <= initial) throw new AcceptanceException("Prepared needs activity did not physically improve its need.");
                s.Report["stage-" + stage] = new JsonObject { ["kind"] = kind, ["predicted"] = predicted,
                    ["observed"] = observed, ["signedError"] = observed - predicted, ["serviceTicks"] = service,
                    ["travelTicks"] = intervals.Where(r => r.Activity is "travel" or "carrying").Sum(r => r.End - r.Start),
                    ["unknownTicks"] = intervals.Where(r => r.Gap || r.Activity == "unknown").Sum(r => r.End - r.Start),
                    ["validity"] = "Current category/tolerance only; service intervals include job setup; needs update discretely. Residuals are evidence, not calibrated risk." };
            }
        },
    };
}
