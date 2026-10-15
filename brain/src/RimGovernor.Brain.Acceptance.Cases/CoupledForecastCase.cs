using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

internal static class CoupledForecastCase
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "observations/coupled-forecast",
        Scope = "Prepared existing rice, simple/bulk bills and unpowered heater verify coherent native tick, exact batch counts, source expiry and physical heat/roof observations. Unit tests cannot establish native mechanics or schema. Does not prove dispatch, thermal calibration or emergent play.",
        Start = new FixtureStart { Op = "test/pawn_cooking_prepare", On = new LabStart() },
        RequiredOps = ["test/pawn_cooking_prepare", "test/building_temperature_prepare", "test/coupled_forecast_read"],
        QuietWorld = true,
        Crew = new Crew { Size = 3 },
        Budget = TimeSpan.FromMinutes(2),
        Run = async (s, ct) =>
        {
            _ = await s.Harness.CallAsync("heater", "test/building_temperature_prepare", [], ct).ConfigureAwait(false);
            var read = await s.Harness.CallAsync("coupled", "test/coupled_forecast_read", [], ct).ConfigureAwait(false);
            if (read["success"]?.GetValue<bool>() != true) throw new AcceptanceException("Coupled native census failed: " + read.ToJson());
            s.Report["observations"] = read.DeepClone();
        },
    };
}
