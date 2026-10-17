using System.Globalization;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Placement = RimGovernor.Contracts.Wire.Placement;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>clearance/pack-reinstall</c> (#2118): the round trip a bedroom reconcile drives, over its native half. A damaged
/// Bed in one staged room is designated for uninstall (relocate, uninstall), vanilla minifies it, and the packed piece is
/// installed by its inner building's id into another staged room (relocate, destination); the same building stands
/// there with its hit points kept. Native: the uninstall and install orders, their blueprints and the pawns' jobs. The
/// planner's reconcile choice is covered by its policy tests.
/// </summary>
internal static class ClearancePackReinstall
{
    private const int HitPoints = 50;

    private static readonly Fx.StageCell From = new(3, 3);
    private static readonly Fx.StageCell To = new(9, 3);

    internal static TypedCase Case() => new()
    {
        Name = "clearance/pack-reinstall",
        Scope = "A damaged Bed in a staged roofed room is uninstalled (relocate, uninstall) and its packed piece installed by the "
            + "building's id into a second staged room (relocate, destination): the same building id stands there, built, with "
            + "its hit points unchanged.",
        Ticks = 8000,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 13,
                Height = 7,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 6,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed, Fx.StageGround.Unzoned],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 13, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Pawns, Fx.StageClearKind.Roofs, Fx.StageClearKind.Zones] }],
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 13, 7), Home = true }],
            Rooms =
            [
                new Fx.StageRoom { Name = "shelter", Rect = new Fx.StageRect(1, 1, 5, 5), Stuff = "WoodLog", Doors = [new(3, 1)], Roof = true },
                new Fx.StageRoom { Name = "bedroom", Rect = new Fx.StageRect(7, 1, 5, 5), Stuff = "WoodLog", Doors = [new(9, 1)], Roof = true },
            ],
            Buildings = [new Fx.StageBuilding { Name = "bed", Def = "Bed", Stuff = "WoodLog", Cells = [From], HitPoints = HitPoints }],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var site = s.Staged!.Site ?? throw new AcceptanceException("stage: no site");
        var bed = s.Staged.Buildings["bed"].Ids.Single();
        var to = new Common.Cell { X = site.X + To.X, Z = site.Z + To.Z };

        async Task<Observations.BuildingState?> ReadAsync(string label)
        {
            var rows = await s.BuildingsAsync(label, new Observations.ListBuildingsRequest { Ids = [bed], Statuses = [Observations.BuildingStatus.Built] }, cancellationToken).ConfigureAwait(false);
            return rows.FirstOrDefault(r => r.Building.Id == bed);
        }

        var start = await ReadAsync("bed-start").ConfigureAwait(false);
        if (start is not { HitPoints: HitPoints, MaxHitPoints: > HitPoints })
        {
            throw new AcceptanceException($"bed-start: want the staged bed at {HitPoints} hit points below its maximum, got {start}");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "uninstall", new Operations.RelocateIntent { ThingId = bed, Uninstall = true }, cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("packed", 3000, async ct =>
        {
            var row = await ReadAsync("packed-probe").ConfigureAwait(false);
            return (Wait.Signature(row is null), row is null);
        }, cancellationToken).ConfigureAwait(false);

        _ = await TypedApply.AppliedAsync(s, "install", new Operations.RelocateIntent { ThingId = bed, Destination = to, Rotation = Placement.Rotation.North }, cancellationToken).ConfigureAwait(false);
        Observations.BuildingState? end = null;
        _ = await s.RunUntilAsync("installed", s.TicksLeft, async ct =>
        {
            end = await ReadAsync("installed-probe").ConfigureAwait(false);
            var placed = end?.Occupied.Minimum is { X: { } x, Z: { } z } && x == to.X && z == to.Z;
            return (Wait.Signature(end is not null, placed), placed);
        }, cancellationToken).ConfigureAwait(false);

        s.RecordWire("bed-end", end);
        if (end?.HitPoints != start.HitPoints)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the round trip changed the bed's hit points: {start.HitPoints} -> {end?.HitPoints}"));
        }
    }
}
