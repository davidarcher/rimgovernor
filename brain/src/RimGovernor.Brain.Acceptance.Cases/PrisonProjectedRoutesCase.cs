using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>A paused native read proves that projected prison walls/doors and round trips reach the shared route boundary.</summary>
internal static class PrisonProjectedRoutesCase
{
    internal static TypedCase Case() => new()
    {
        Name = "observations/prison-projected-routes",
        Scope = "A live wall ring has no entrance. Projecting a door permits the warden's inbound interaction walk and return; a farther work origin costs more, while the intact ring remains unreachable. No live building is changed.",
        Ticks = 1, Tier = CaseTier.Nightly, Start = new LabStart { Colonists = 1 }, QuietWorld = true,
        Stage = new()
        {
            Paused = true,
            Site = new() { Width = 17, Height = 11, OffsetX = 6, Radius = 20,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Heavy] },
            Clear = [new() { Rect = new(0, 0, 17, 11), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
            Buildings = [new() { Name = "ring", Def = "Wall", Stuff = "Steel", Cells = EquatableArray.From(
                Enumerable.Range(5, 5).SelectMany(x => new[] { new Fx.StageCell(x, 2), new Fx.StageCell(x, 6) })
                .Concat(Enumerable.Range(3, 3).SelectMany(z => new[] { new Fx.StageCell(5, z), new Fx.StageCell(9, z) }))) }],
            Pawns = [new() { Name = "warden", Colonist = 0, Cell = new(1, 4), AllWork = 0, Timetable = Fx.StageTimeAssignment.Work }],
        },
        Ops = [FixtureStage.Tool], Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession session, CancellationToken ct)
    {
        var staged = session.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("The prison route stage has no site.");
        Cell At(int x, int z) => new(site.X + x, site.Z + z);
        var bed = At(7, 4);
        var near = At(1, 4);
        var far = At(1, 9);
        var inside = EquatableArray.From(Enumerable.Range(6, 3).SelectMany(x => Enumerable.Range(3, 3).Select(z => At(x, z))));
        var opened = new PlannedRoomShape("1", "Steel", inside, [new(At(5, 4), "Door", "Steel")])
            { Furniture = [new(bed, "Bed", "Steel", Rotation.North)] };
        var sealedRoom = opened with { Stuff = "WoodLog", Pieces = [] };
        var read = await session.CallAsync("projected-prison", NativeOps.ReadConstructionActivity,
            new ConstructionActivityRequest(session.Identity, [])
            {
                RoomPredictions = [opened, sealedRoom],
                Routes = [new(near, bed, []) { Projection = opened }, new(near, bed, []) { Projection = opened, Return = true },
                    new(far, bed, []) { Projection = opened }, new(far, bed, []) { Projection = opened, Return = true },
                    new(near, bed, []) { Projection = sealedRoom }, new(near, bed, []) { Projection = sealedRoom, Return = true }],
            }, ct).ConfigureAwait(false);
        session.RecordWire("projected-prison", read);
        var warden = staged.Pawns["warden"].Id;
        var routes = read.RoomPredictions.Single(p => p.Stuff == "Steel").Routes.Where(r => r.Pawn == warden).ToArray();
        double Cost(Cell from, Cell to) => routes.Single(r => r.From == from && r.To == to).Ticks is RouteTicks.Reachable path
            ? path.Ticks : throw new AcceptanceException("A projected door did not open the prison route.");
        if (!(Cost(far, bed) > Cost(near, bed) && Cost(bed, far) > Cost(bed, near)))
            throw new AcceptanceException("The farther prison trip did not cost more in both directions.");
        var blocked = read.RoomPredictions.Single(p => p.Stuff == "WoodLog").Routes.Where(r => r.Pawn == warden).ToArray();
        if (blocked.Length != 2 || blocked.Any(r => r.Ticks is not RouteTicks.Unreachable))
            throw new AcceptanceException("An intact native wall ring was treated as a reachable prison.");
    }
}
