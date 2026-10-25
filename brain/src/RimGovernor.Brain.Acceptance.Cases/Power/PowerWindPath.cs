using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>power/wind-path</c>: the native wind-path read the power planner sites turbines by. Two staged wind turbines
/// stand side by side, one with constructed roof over part of the open field behind it; observations_list_buildings
/// reads no blocked wind cells for the clear one and some for the roofed one. The planner's turbine siting over that
/// read is covered by the power policy tests.
/// </summary>
internal static class PowerWindPath
{
    private const string Clear = "clear";
    private const string Roofed = "roofed";

    // A north-facing turbine's wind field runs three cells either side of its centre and from six cells south to
    // eleven north: the site holds both fields whole.
    internal static TypedCase Case() => new()
    {
        Name = "power/wind-path",
        Scope = "BuildingServiceState.wind_path_blocked_cells on observations_list_buildings: a staged wind turbine with an "
            + "open field reads 0 blocked cells, and one with constructed roof over part of its field reads more than 0. Read-only.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 20,
                Height = 18,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 8,
                Radius = 40,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
            },
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(13, 12, 7, 6), Roof = "RoofConstructed" }],
            Buildings =
            [
                new Fx.StageBuilding { Name = Clear, Def = "WindTurbine", Cells = [new(3, 6)] },
                new Fx.StageBuilding { Name = Roofed, Def = "WindTurbine", Cells = [new(16, 6)] },
            ],
        },
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var clearId = s.Staged!.Buildings[Clear].Ids.Single();
        var roofedId = s.Staged.Buildings[Roofed].Ids.Single();
        var rows = await s.BuildingsAsync("turbines", new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { Ids = [clearId, roofedId] }, cancellationToken).ConfigureAwait(false);
        uint? Blocked(string id) => rows.FirstOrDefault(r => r.Building?.Id == id)?.Service?.Wind is RimGovernor.Brain.GameFacts.Observations.BuildingWind.WindTurbine turbine ? turbine.BlockedCells : null;
        var clear = Blocked(clearId);
        var roofed = Blocked(roofedId);
        s.Record("clear_blocked", clear);
        s.Record("roofed_blocked", roofed);
        if (clear != 0 || roofed is not > 0)
        {
            throw new AcceptanceException($"wind path: want 0 blocked cells for the clear turbine and some for the roofed one, got {clear} / {roofed}");
        }
    }
}
