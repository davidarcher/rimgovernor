using System.Globalization;
using RimGovernor.Brain.NativeFacade;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>storage/occupancy_ring</c> (#3234): the native occupancy ring samples a stockpile's held stacks every game hour
/// and keeps them across a save and reload. A unit test cannot prove it: the samples come from the native map
/// component's hourly tick over the real haul-destination groups, and survival from the game's own save.
/// </summary>
internal static class StorageOccupancyRing
{
    internal static TypedCase Case() => new()
    {
        Name = "storage/occupancy_ring",
        Scope = "Native occupancy ring over a staged one-cell stockpile holding a Steel stack: 6000 ticks cross two hour boundaries "
            + "(at least two samples, the last one stack), and the same samples read back after a save and reload.",

        // 6000 ticks cross at least two hour boundaries (2500 ticks each).
        Ticks = 6000,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 4,
                Height = 4,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 8,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed, Fx.StageGround.Unzoned],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 4, 4), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
            Zones = [new Fx.StageZone { Name = "store", Kind = Fx.StageZoneKind.Stockpile, Cells = [new(1, 1)], Allow = ["Steel"] }],
            Things = [new Fx.StageThing { Name = "steel", Def = "Steel", Count = 50, Cell = new(1, 1) }],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var zone = s.Staged!.Zones["store"];
        await s.RunTicksAsync("hours", 6000, cancellationToken).ConfigureAwait(false);

        var before = await SamplesAsync(s, zone, "occupancy-before", cancellationToken).ConfigureAwait(false);
        if (before.Count < 2 || before[^1].Stacks != 1)
        {
            throw new AcceptanceException($"want at least two hourly samples of {zone}, the last holding the one Steel stack, got [{Render(before)}]");
        }

        await s.ReloadAsync("occupancy-reload", cancellationToken).ConfigureAwait(false);
        var after = await SamplesAsync(s, zone, "occupancy-after", cancellationToken).ConfigureAwait(false);
        if (after.Count < before.Count || !after.Take(before.Count).SequenceEqual(before))
        {
            throw new AcceptanceException($"the reload lost samples of {zone}: before [{Render(before)}], after [{Render(after)}]");
        }
    }

    private static async Task<List<Observations.StorageOccupancySample>> SamplesAsync(ITypedSession s, string zone, string label, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadStorageOccupancy, new Observations.StorageOccupancyRequest { Groups = [new Common.Ref { Id = zone }] }, cancellationToken).ConfigureAwait(false);
        var snapshot = TypedReads.Observed(label, NativeOps.ReadStorageOccupancy, reply, reply.Outcome?.Observed);
        s.RecordWire(label, snapshot);
        return [.. snapshot.Groups.Where(g => g.Group?.Id == zone).SelectMany(g => g.Samples)];
    }

    private static string Render(List<Observations.StorageOccupancySample> samples) =>
        string.Join(", ", samples.Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Hour}:{x.Stacks}")));
}
