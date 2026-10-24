using System.Globalization;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>farm/plantation-rows</c>: a sown oak in an oak growing zone is a plantation row of the colony facts' acquisition
/// census, carrying its growth fraction; a sown rice plant in the same zone is not a row (#2292).
/// </summary>
internal static class FarmPlantation
{
    private const string Oak = "oak";
    private const string Rice = "rice";

    internal static TypedCase Case() => new()
    {
        Name = "farm/plantation-rows",
        Scope = "observations_read_colony_facts (planning) over a staged oak growing zone holding a sown Plant_TreeOak at growth "
            + "0.8 and a sown Plant_Rice at 0.9: the oak is an acquisition row marked plantation with growth 0.8, and the rice "
            + "crop is not a row (#2292). Read-only.",
        Ticks = 0,
        Tier = CaseTier.Smoke,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 2,
                Height = 1,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 20,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
            },
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 2, 1), Terrain = "Soil" }],
            Plants =
            [
                new Fx.StagePlant { Name = Oak, Def = "Plant_TreeOak", Cells = [new(0, 0)], Growth = 0.8f, Sown = true },
                new Fx.StagePlant { Name = Rice, Def = "Plant_Rice", Cells = [new(1, 0)], Growth = 0.9f, Sown = true },
            ],
            Pawns = [new Fx.StagePawn { Name = "cutter", Colonist = 0, Work = new Dictionary<string, int> { ["PlantCutting"] = 1 } }],
            Zones = [new Fx.StageZone { Name = "field", Kind = Fx.StageZoneKind.Growing, Plant = "Plant_TreeOak", Cells = [new(0, 0), new(1, 0)] }],
        },
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var oakId = s.Staged!.Plants[Oak].Ids.Single();
        var riceId = s.Staged.Plants[Rice].Ids.Single();
        var reply = await s.CallAsync("plantation-facts", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = true }, cancellationToken).ConfigureAwait(false);
        var rows = reply.Acquisition.ToList();
        if (rows.FirstOrDefault(r => r.Source?.Id == riceId) is { } riceRow)
        {
            throw new AcceptanceException($"a rice plant in the growing zone is an acquisition row: {riceRow}");
        }

        var oakRow = rows.FirstOrDefault(r => r.Source?.Id == oakId)
            ?? throw new AcceptanceException($"the sown oak {oakId} is not an acquisition row among {rows.Count}");
        s.RecordWire("oak_row", oakRow);
        if (oakRow.Kind is not Observations.AcquisitionKind.Plant { Plantation: true, Growth: >= 0.79 and <= 0.81 })
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the oak row is not a plantation row at the staged growth 0.8: {oakRow}"));
        }
    }
}
