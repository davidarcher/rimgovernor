using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>route/stockpile</c> (#3265): the upkeep routes census the layout/routes Need reads, over two staged roofed home
/// rooms each holding a stockpile, one sealed and one with a door. The census paths with the game's own pathing per
/// colonist, so only a live game shows the sealed stockpile unreachable with its walls as breaches and the open one
/// reached by a measured path. The Need's door choice is covered by the routes policy tests.
/// </summary>
internal static class RouteStockpile
{
    private static readonly Fx.StageRect Sealed = new(1, 1, 5, 5);
    private static readonly Fx.StageRect Open = new(7, 1, 5, 5);

    internal static TypedCase Case() => new()
    {
        Name = "route/stockpile",
        Scope = "The upkeep routes census over a staged sealed roofed room and an open one, each holding a stockpile: the "
            + "sealed stockpile reads kind STOCKPILE with every mobile colonist unreachable and breach walls on its own border "
            + "only; the open one reads reachable by a measured path with no breaches.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,

        // The census paths between the colonists and the staged rooms; the wild map is unobserved.
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
                new Fx.StageRoom { Name = "sealed", Rect = Sealed, Stuff = "WoodLog", Roof = true },
                new Fx.StageRoom { Name = "open", Rect = Open, Stuff = "WoodLog", Doors = [new(9, 1)], Roof = true },
            ],
            Zones =
            [
                new Fx.StageZone { Name = "sealed", Kind = Fx.StageZoneKind.Stockpile, Cells = Interior(Sealed) },
                new Fx.StageZone { Name = "open", Kind = Fx.StageZoneKind.Stockpile, Cells = Interior(Open) },
            ],
        },
        Run = RunAsync,
    };

    private static Fx.StageCell[] Interior(Fx.StageRect r) =>
        [.. from x in Enumerable.Range(r.X + 1, r.Width - 2) from z in Enumerable.Range(r.Z + 1, r.Height - 2) select new Fx.StageCell(x, z)];

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("stage: no site");
        var upkeep = await TypedApply.UpkeepAsync(s, "routes", cancellationToken).ConfigureAwait(false);
        var routes = upkeep.Routes;
        var colonists = routes.PawnIds.Count;
        var problems = new List<string>();

        Observations.RouteFacility Facility(string name)
        {
            var interior = staged.Rooms[name].Interior.ToHashSet();
            var row = routes.Facilities.FirstOrDefault(f => f.Kind == Observations.RouteFacilityKind.Stockpile && f.Cell is Common.Cell { X: { } x, Z: { } z } && interior.Contains(new(x, z)))
                ?? throw new AcceptanceException($"routes: no STOCKPILE facility in the {name} room");
            s.RecordWire(name, row);
            return row;
        }

        var sealedRow = Facility("sealed");
        if (colonists == 0 || sealedRow.Travel.Count != colonists || sealedRow.Travel.Any(t => t.Reachable == true))
        {
            problems.Add($"sealed: want {colonists} travel rows, none reachable, got {string.Join("; ", sealedRow.Travel)}");
        }

        bool OnBorder(Common.Cell? c, Fx.StageRect r)
        {
            var (x, z) = ((c?.X ?? -1) - site.X, (c?.Z ?? -1) - site.Z);
            var inside = x >= r.X && x < r.X + r.Width && z >= r.Z && z < r.Z + r.Height;
            var interior = x > r.X && x < r.X + r.Width - 1 && z > r.Z && z < r.Z + r.Height - 1;
            return inside && !interior;
        }

        if (sealedRow.Breaches.Count == 0 || sealedRow.Breaches.Any(b => b.Edifice != "Wall" || !OnBorder(b.Cell, Sealed)))
        {
            problems.Add($"sealed: want breach walls on its border only, got {string.Join("; ", sealedRow.Breaches)}");
        }

        var openRow = Facility("open");
        if (!openRow.Travel.Any(t => t is { Reachable: true, PathCells: > 0 }) || openRow.Breaches.Count != 0)
        {
            problems.Add($"open: want a measured reachable path and no breaches, got {string.Join("; ", openRow.Travel)} / {string.Join("; ", openRow.Breaches)}");
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }
}
