using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Placement = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>layout/power-sites</c> (#838): the footprint conventions <see cref="LayoutUtilityRules.PlannedPowerSites"/> assumes
/// for battery and solar sites, against the cells RimWorld's placement_preview reports at the same centre and rotation.
/// The plan is not needed: only the def sizes and the game's rotation math decide the footprint, and only a live game
/// has them.
/// </summary>
internal static class LayoutPowerSites
{
    // Each planned convention: the def, its centre and rotation on the site, and the area the plan books for it.
    // A battery facing north spans z..z+1, turned east x..x+1; a solar site's centre is its area's corner plus (1, 1).
    private static readonly (string Def, Fx.StageCell Cell, Placement.Rotation Rotation, Fx.StageRect Area)[] Sites =
    [
        (PowerRules.BatteryDefinition, new(2, 2), Placement.Rotation.North, new(2, 2, 1, 2)),
        (PowerRules.BatteryDefinition, new(2, 6), Placement.Rotation.East, new(2, 6, 2, 1)),
        (LayoutUtilityRules.SolarDefinition, new(7, 3), Placement.Rotation.North, new(6, 2, 4, 4)),
    ];

    internal static TypedCase Case() => new()
    {
        Name = "layout/power-sites",
        Scope = "placement_preview of a Battery facing north and turned east and a SolarGenerator facing north on a staged "
            + "clear site: each native footprint is exactly the area PlannedPowerSites books for that centre and rotation "
            + "(#838). Read-only.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 12,
                Height = 10,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 8,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged],
            },
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var site = s.Staged!.Site ?? throw new AcceptanceException("stage: no site");
        var problems = new List<string>();
        foreach (var (def, cell, rotation, area) in Sites)
        {
            var label = $"preview-{def}-{rotation}";
            var reply = await s.CallAsync(label, NativeOps.PreviewPlacement, new Placement.PlacementRequest
            {
                Identity = s.Identity,
                Placements = [new Placement.PlacementCandidate { DefName = def, X = site.X + cell.X, Z = site.Z + cell.Z, Rotation = rotation }],
            }, cancellationToken).ConfigureAwait(false);
            var rotations = reply.Outcome?.Batch?.Results.ToList() is [{ Outcome.Evaluated: { } evaluated }] ? evaluated.Rotations.ToList() : [];
            if (rotations is not [var row])
            {
                problems.Add($"{label}: want one evaluated rotation, got {reply.Outcome}");
                continue;
            }

            s.RecordWire(label, row);
            var got = row.OccupiedCells.Select(c => new Fx.StageCell(c.X - site.X, c.Z - site.Z)).ToHashSet();
            var want = (from x in Enumerable.Range(area.X, area.Width) from z in Enumerable.Range(area.Z, area.Height) select new Fx.StageCell(x, z)).ToHashSet();
            if (!got.SetEquals(want))
            {
                problems.Add($"{label} at {cell}: native footprint [{string.Join(' ', got)}] is not the planned area {area}");
            }
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}
