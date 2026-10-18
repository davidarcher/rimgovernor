using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/sow-block</c> (#3528): a staged capable sower and an empty rice growing zone of twelve cells. Under Auto
/// authority one exact Sow step over four cells is installed straight into the native execution table (nothing values sowing
/// yet, so the brain selects none itself). The step repeats one native Sow job per cell, so the zone ends with exactly four
/// plants and the execution completes.
/// </summary>
internal static class ProductionSowBlock
{
    private const string PrepareTool = "test/pawn_sow_prepare";
    private const string InstallTool = "test/pawn_sow_install";
    private const string ReadTool = "test/pawn_sow_read";
    private const int Cells = 4;

    private static readonly FixtureOp<Fx.StageReply, Fx.FixtureDone> Prepare = new(
        PrepareTool,
        r => ServedOps.Args(("sower", r.Pawns["sower"].Id), ("zone", r.Zones["field"])),
        ServedOps.Done(PrepareTool));

    private static readonly FixtureOp<int, Fx.FixtureDone> Install = new(InstallTool, count => ServedOps.Args(("count", count)), ServedOps.Done(InstallTool));
    private static readonly FixtureOp<NoArgs, Fx.SowRead> Read = ServedOps.Read<Fx.SowRead>(ReadTool);

    internal static TypedCase Case() => new()
    {
        Name = "production/sow-block",
        Scope = "One exact Sow step over four cells of a twelve-cell growing zone, installed under Auto authority, sows exactly four "
            + "plants and completes: native repeats one Sow job per cell because JobDriver_PlantSow sows a single cell and ignores "
            + "queued targets. A unit test cannot prove the native legality read, the per-cell repetition or the planted count.",
        Ticks = 6000,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 8,
                Height = 5,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 8, 5), Kinds = [Fx.StageClearKind.Things] }],
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 8, 5), Terrain = "Soil" }],
            Pawns = [new Fx.StagePawn { Name = "sower", Colonist = 0, Needs = new Dictionary<string, float> { ["Food"] = 1f, ["Rest"] = 1f } }],
            Zones =
            [
                new Fx.StageZone
                {
                    Name = "field",
                    Kind = Fx.StageZoneKind.Growing,
                    Plant = "Plant_Rice",
                    Cells = [.. Enumerable.Range(0, 4).SelectMany(x => Enumerable.Range(0, 3).Select(z => new Fx.StageCell(2 + x, 1 + z)))],
                },
            ],
        },
        Ops = [PrepareTool, InstallTool, ReadTool],
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            _ = await s.FixtureAsync("prepare", Prepare, s.Staged!, ct).ConfigureAwait(false);
            await TypedAuthority.GrantAutoAsync(s, ct).ConfigureAwait(false);
            _ = await s.FixtureAsync("install", Install, Cells, ct).ConfigureAwait(false);
            Fx.SowRead? last = null;
            _ = await s.RunUntilAsync("sow", s.TicksLeft, async c =>
            {
                last = await s.FixtureAsync("sow-read", Read, NoArgs.Value, c).ConfigureAwait(false);
                if (last.Forced || last.Execution?.State == "failed")
                {
                    throw new AcceptanceException("the sow step became player-forced or failed: " + ServedOps.Json(last));
                }

                return ($"{last.Execution?.State} {last.Sown}", last.Execution?.State == "completed");
            }, ct).ConfigureAwait(false);
            s.Record("sown", ServedOps.Json(last));
            if (last?.Execution?.State != "completed" || last.Sown != Cells)
            {
                throw new AcceptanceException($"the sow step did not sow exactly {Cells} cells and complete: " + ServedOps.Json(last));
            }
        },
    };
}
