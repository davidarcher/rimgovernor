using System.Globalization;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>farm/lattice-sow</c>: issue #2290. A growing zone of trees (blockAdjacentSow) sown by colonists ends with exactly
/// ceil(N/2)*ceil(M/2) trees, every one on the 2x2 lattice anchored on the zone's minimum corner, while a rice zone of
/// the same size is filled completely. The rule is a Harmony patch on vanilla WorkGiver_GrowerSow.JobOnCell and the
/// sow-blocking is vanilla's own (PlantUtility.AdjacentSowBlocker), so only the game can show it.
/// </summary>
internal static class FarmLattice
{
    private const string OakZone = "oak";
    private const string RiceZone = "rice";
    private const int Width = 5;
    private const int Height = 4;

    internal static TypedCase Case() => new()
    {
        Name = "farm/lattice-sow",
        Scope = "Issue #2290: colonists on Growing sow a staged empty 5x4 Plant_TreeOak zone with exactly ceil(5/2)*ceil(4/2) trees, "
            + "every one on the 2x2 lattice anchored on the zone's minimum corner (the WorkGiver_GrowerSow.JobOnCell patch over "
            + "vanilla's AdjacentSowBlocker), while an equal Plant_Rice zone two cells away fills every cell; read through the "
            + "zones and cells reads.",
        Ticks = TypedCase.StagedTickCap,
        Tier = CaseTier.Nightly,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = (Width * 2) + 4,
                Height = Height + 2,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 3,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, (Width * 2) + 4, Height + 2), Kinds = [Fx.StageClearKind.Things] }],
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, (Width * 2) + 4, Height + 2), Terrain = "Soil" }],
            Pawns = [.. Enumerable.Range(0, 3).Select(i => new Fx.StagePawn
            {
                Name = $"grower-{i}",
                Colonist = i,
                AllWork = 0,
                Work = new Dictionary<string, int> { ["Growing"] = 1 },
                Timetable = Fx.StageTimeAssignment.Work,
            })],
            Zones =
            [
                new Fx.StageZone { Name = OakZone, Kind = Fx.StageZoneKind.Growing, Plant = "Plant_TreeOak", Cells = Rect(1, 1) },
                new Fx.StageZone { Name = RiceZone, Kind = Fx.StageZoneKind.Growing, Plant = "Plant_Rice", Cells = Rect(Width + 3, 1) },
            ],
        },
        Run = RunAsync,
    };

    private static List<Fx.StageCell> Rect(int x0, int z0) =>
        [.. Enumerable.Range(0, Width).SelectMany(x => Enumerable.Range(0, Height).Select(z => new Fx.StageCell(x0 + x, z0 + z)))];

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var oakId = s.Staged!.Zones[OakZone];
        var riceId = s.Staged.Zones[RiceZone];
        var pitch = DomainConstants.TreeLatticePitch;
        var wantTrees = (Width + pitch - 1) / pitch * ((Height + pitch - 1) / pitch);
        var wantRice = Width * Height;

        GrowingZone? oak = null;
        GrowingZone? rice = null;
        _ = await s.RunUntilAsync("lattice-sow", s.TicksLeft, async ct =>
        {
            var zones = await s.GrowingZonesAsync("lattice-census", ct).ConfigureAwait(false);
            oak = zones.FirstOrDefault(z => z.Id == oakId);
            rice = zones.FirstOrDefault(z => z.Id == riceId);
            var oakCount = oak?.Planted.Count ?? 0;
            var riceCount = rice?.Planted.Count ?? 0;
            return (Wait.Signature(oakCount, riceCount), oakCount >= wantTrees && riceCount >= wantRice);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("oak_planted", oak?.Planted.Count);
        s.Record("rice_planted", rice?.Planted.Count);

        var minX = oak!.Cells.Min(c => c.X);
        var minZ = oak.Cells.Min(c => c.Z);
        if (oak.Planted.Count != wantTrees)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"oak zone holds {oak.Planted.Count} trees, want ceil({Width}/2)*ceil({Height}/2)={wantTrees}"));
        }

        if (oak.Planted.FirstOrDefault(c => (c.X - minX) % pitch != 0 || (c.Z - minZ) % pitch != 0) is { } off)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"oak at ({off.X},{off.Z}) is off the lattice anchored at ({minX},{minZ})"));
        }

        if (rice!.Planted.Count != wantRice)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"rice zone holds {rice.Planted.Count} plants, want every one of {wantRice} cells"));
        }
    }
}
