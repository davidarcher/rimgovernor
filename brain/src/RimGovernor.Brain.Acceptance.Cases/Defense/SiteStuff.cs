using RimGovernor.Brain.NativeFacade;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>defense/site-stuff</c> (#1065): one finished player wall of each of four stuffs is staged on the lab, and the
/// defense-site census reads each wall's cell back with the stuff the building census gives it. The wait hardening tells a
/// wooden door from a plasteel one by this field. Read-only: no orders, no clock.
/// </summary>
internal static class DefenseSiteStuff
{
    /// <summary>One wall per stuff, two cells apart along the site's row.</summary>
    private static readonly string[] s_stuffs = ["WoodLog", "BlocksGranite", "Steel", "Plasteel"];

    private const int Width = 7;

    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = Width,
            Height = 1,
            OffsetX = 6,
            OffsetZ = 6,
            Radius = 10,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged, Fx.StageGround.Heavy],
        },
        Buildings = [.. s_stuffs.Select((stuff, i) => new Fx.StageBuilding { Name = stuff, Def = "Wall", Stuff = stuff, Cells = [new(2 * i, 0)] })],
    };

    internal static TypedCase Case() => new()
    {
        Name = "defense/site-stuff",
        Scope = "The defense-site census carries each colonist wall's stuff def (#1065): staged walls of wood, granite blocks, steel and "
            + "plasteel each read back from observations_read_defense_site with the stuff observations_list_buildings gives that wall; read-only.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = Spec,
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("test/stage replied no site");
        var walls = await s.BuildingsAsync("walls", new Observations.ListBuildingsRequest { DefNames = ["Wall"], Statuses = ["built"], PlayerOnly = true }, cancellationToken).ConfigureAwait(false);
        var reply = await s.CallAsync("defense-site", NativeOps.ReadDefenseSite, new Observations.DefenseSiteRequest
        {
            Scope = s.Scope,
            Region = new Observations.Rectangle
            {
                Minimum = new Common.Cell { X = site.X, Z = site.Z },
                Maximum = new Common.Cell { X = site.X + Width - 1, Z = site.Z },
            },
        }, cancellationToken).ConfigureAwait(false);
        var census = TypedReads.Observed("defense-site", NativeOps.ReadDefenseSite, reply, reply.Outcome?.Observed);
        var problems = new List<string>();
        foreach (var stuff in s_stuffs)
        {
            var cell = staged.Buildings.GetValueOrDefault(stuff)?.Cells is [var only] ? only : throw new AcceptanceException($"test/stage placed no single {stuff} wall");
            var building = walls.FirstOrDefault(w => w.Occupied is { Minimum: { } min, Maximum: { } max } && min.X == cell.X && min.Z == cell.Z && max.X == cell.X && max.Z == cell.Z);
            var row = census.Cells.FirstOrDefault(c => c.Cell is { } at && at.X == cell.X && at.Z == cell.Z);
            s.Record(stuff, $"buildings {building?.Stuff ?? "missing"}, census {row?.EdificeDefName ?? "missing"}/{row?.EdificeStuffDefName ?? "missing"}");
            if (building?.Stuff != stuff)
            {
                problems.Add($"wall at {cell} staged of {stuff}: the building census reads {building?.Stuff ?? "no wall"}");
            }
            else if (row?.EdificeDefName != "Wall" || row.EdificeStuffDefName != building.Stuff)
            {
                problems.Add($"wall at {cell} of {building.Stuff}: the defense-site census reads {row?.EdificeDefName ?? "no cell"}/{row?.EdificeStuffDefName ?? "no stuff"}");
            }
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}
