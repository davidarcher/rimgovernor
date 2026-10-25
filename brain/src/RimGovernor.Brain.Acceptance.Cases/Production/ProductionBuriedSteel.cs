using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/buried-steel</c> and <c>production/buried-steel-stockpile</c>: the native mining rules behind the steel
/// floor (#1075, #1133). The first reads observations_read_excavation_site over a staged thick-roofed granite block: a
/// face deposit and a fogged buried deposit both come out supported (the verdict reads the true map through fog), and
/// hollowing the block is not. The second mines a staged steel cell beside a stockpile under the acquisition guard and
/// reads the replacement wall blueprint the guard places on the mined cell. The planner's choice to mine is covered by
/// the resource policy tests; the roof verdict and the wall are native, so only a live game shows them.
/// </summary>
internal static class ProductionBuriedSteel
{
    // The block's columns start past three open columns the miner stands on.
    private const int BlockX = 3;
    private const int BlockSide = 17;

    private static readonly Fx.StageCell[] Face = [new(BlockX, 5), new(BlockX, 6)];

    // Five columns into the block: fogged, and out of every colonist's reach.
    private static readonly Fx.StageCell[] Buried = [new(BlockX + 5, 10), new(BlockX + 5, 11)];

    internal static IEnumerable<TypedCase> Cases()
    {
        yield return new TypedCase
        {
            Name = "production/buried-steel",
            Scope = "observations_read_excavation_site over a staged granite block under thick rock roof: the face steel deposit "
                + "and the fogged buried deposit each read MineableSteel and SUPPORTED after removal (roof support read through "
                + "fog), the buried cells read fogged, and hollowing the block's interior reads UNSUPPORTED (#1075). Read-only.",
            Ticks = 0,
            Tier = CaseTier.OnDemand,
            Stage = BlockStage(),
            QuietWorld = true,
            Run = ReadBlockAsync,
        };

        yield return new TypedCase
        {
            Name = "production/buried-steel-stockpile",
            Scope = "A MINE designate under the acquisition guard on a staged steel cell beside a stockpile: a colonist mines it, "
                + "and the guard follows the mined cell, which opens colony space, with a Wall blueprint on it (#1133).",
            Ticks = 15000,
            Tier = CaseTier.OnDemand,
            Stage = FaceStage(),
            QuietWorld = true,
            Run = MineBesideStockpileAsync,
        };
    }

    // The block: granite under thick roof, fogged past its face column, with the two steel deposits.
    private static Fx.StageSpec BlockStage()
    {
        var deposits = Face.Concat(Buried).ToHashSet();
        var granite = (from x in Enumerable.Range(BlockX, BlockSide)
                       from z in Enumerable.Range(0, BlockSide)
                       select new Fx.StageCell(x, z)).Where(c => !deposits.Contains(c)).ToList();
        return new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = BlockX + BlockSide,
                Height = BlockSide,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 10,
                Radius = 40,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
            },
            Cells =
            [
                new Fx.StageCells { Rect = new Fx.StageRect(BlockX, 0, BlockSide, BlockSide), Roof = "RoofRockThick" },
                new Fx.StageCells { Rect = new Fx.StageRect(BlockX + 1, 0, BlockSide - 1, BlockSide), Fogged = true },
            ],
            Buildings =
            [
                new Fx.StageBuilding { Name = "granite", Def = "Granite", Cells = granite, Faction = Fx.StageFaction.None },
                new Fx.StageBuilding { Name = "face", Def = "MineableSteel", Cells = Face, Faction = Fx.StageFaction.None },
                new Fx.StageBuilding { Name = "buried", Def = "MineableSteel", Cells = Buried, Faction = Fx.StageFaction.None },
            ],
        };
    }

    private static async Task ReadBlockAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("stage: no site");
        var access = new RimGovernor.Brain.Domain.Cell { X = site.X, Z = site.Z + Face[0].Z };

        async Task<RimGovernor.Brain.GameFacts.Observations.ExcavationSiteSnapshot> ReadAsync(string label, IEnumerable<Fx.StageCell> cells)
        {
            var snapshot = await s.CallAsync(label, NativeOps.ReadExcavationSite, new RimGovernor.Brain.GameOrders.Observations.ExcavationSiteRequest
            {
                Scope = s.Scope,
                Cells = [.. cells.Select(c => new RimGovernor.Brain.Domain.Cell { X = c.X, Z = c.Z })],
                AccessCell = access,
            }, cancellationToken).ConfigureAwait(false);
            s.RecordWire(label, snapshot);
            return snapshot;
        }

        var problems = new List<string>();
        void Deposit(string name, RimGovernor.Brain.GameFacts.Observations.ExcavationSiteSnapshot read, bool fogged)
        {
            if (read.Support != RimGovernor.Brain.GameFacts.Observations.ExcavationSupport.Supported)
            {
                problems.Add($"{name}: want SUPPORTED, got {read.Support} ({read.SupportBlocker})");
            }

            problems.AddRange(read.Cells
                .Where(c => c.Definition != "MineableSteel" || c.Fogged != fogged)
                .Select(c => $"{name}: want MineableSteel fogged={fogged}, got {c}"));
        }

        Deposit("face", await ReadAsync("face", staged.Buildings["face"].Cells).ConfigureAwait(false), fogged: false);
        Deposit("buried", await ReadAsync("buried", staged.Buildings["buried"].Cells).ConfigureAwait(false), fogged: true);

        // Everything but the block's outer ring: the middle is further than the support radius from any holder.
        var hollow = await ReadAsync("hollow", from x in Enumerable.Range(site.X + BlockX + 1, BlockSide - 2)
                                               from z in Enumerable.Range(site.Z + 1, BlockSide - 2)
                                               select new Fx.StageCell(x, z)).ConfigureAwait(false);
        if (hollow.Support != RimGovernor.Brain.GameFacts.Observations.ExcavationSupport.Unsupported)
        {
            problems.Add($"hollowed block: want UNSUPPORTED, got {hollow.Support}");
        }

        if (problems.Count > 0)
        {
            throw new AcceptanceException(string.Join("; ", problems));
        }
    }

    // A granite face three columns deep with one steel cell, a stockpile against it and one miner.
    private static readonly Fx.StageCell Ore = new(5, 3);

    private static Fx.StageSpec FaceStage() => new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 8,
            Height = 7,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 6,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Unroofed],
        },
        Buildings =
        [
            new Fx.StageBuilding
            {
                Name = "granite",
                Def = "Granite",
                Cells = [.. (from x in Enumerable.Range(5, 3) from z in Enumerable.Range(0, 7) select new Fx.StageCell(x, z)).Where(c => c != Ore)],
                Faction = Fx.StageFaction.None,
            },
            new Fx.StageBuilding { Name = "ore", Def = "MineableSteel", Cells = [Ore], Faction = Fx.StageFaction.None },
        ],
        Zones = [new Fx.StageZone { Name = "stockpile", Kind = Fx.StageZoneKind.Stockpile, Cells = [new(3, 2), new(4, 2), new(3, 3), new(4, 3), new(3, 4), new(4, 4)] }],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = "miner",
                Colonist = 0,
                Cell = new(2, 3),
                AllWork = 0,
                Work = new Dictionary<string, int> { ["Mining"] = 1 },
                Timetable = Fx.StageTimeAssignment.Work,
            },
        ],
    };

    private static async Task MineBesideStockpileAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var ore = s.Staged!.Buildings["ore"];
        var at = ore.Cells.Single();
        var cell = new RimGovernor.Brain.Domain.Cell { X = at.X, Z = at.Z };
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var apply = await s.CallAsync("designate", NativeOps.Apply, new RimGovernor.Brain.GameOrders.Operations.ApplyRequest
        {
            Identity = s.Identity,
            Actions =
            [
                new RimGovernor.Brain.GameOrders.Operations.Action
                {
                    Key = "mine-ore",
                    Intent = new RimGovernor.Brain.GameOrders.Operations.DesignateIntent
                    {
                        Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Mine,
                        Target = new RimGovernor.Brain.GameFacts.Common.Ref { Id = ore.Ids.Single() },
                        Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At(cell),
                        Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.Acquisition,
                        ExpectedDef = "Steel", ReplaceWithWall = false, Withdraw = false },
                },
            ], DeferSnapshot = false }, cancellationToken).ConfigureAwait(false);
        if (apply.Results.ToList() is not [{ Outcome.Applied.Outcome.Applied: not null }])
        {
            throw new AcceptanceException($"designate: want one applied mine, got {apply.BatchFailure} {string.Join("; ", apply.Results)}");
        }

        RimGovernor.Brain.GameFacts.Observations.ExcavationCell? row = null;
        RimGovernor.Brain.GameFacts.Observations.BuildingState? wall = null;
        _ = await s.RunUntilAsync("mined", 15000, async ct =>
        {
            var site = await s.CallAsync("ore-cell", NativeOps.ReadExcavationSite, new RimGovernor.Brain.GameOrders.Observations.ExcavationSiteRequest
            {
                Scope = s.Scope,
                Cells = [cell],
                AccessCell = cell,
            }, ct).ConfigureAwait(false);
            row = site.Cells.Single();
            var buildings = await s.BuildingsAsync("blueprints", new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest(), ct).ConfigureAwait(false);
            wall = buildings.FirstOrDefault(b => b is { Status: RimGovernor.Brain.GameFacts.Observations.BuildingStatus.Blueprint, Construction: RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build { Definition: "Wall" } }
                && b.Building.Place is RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap { Cell: var cell } && cell.X == at.X && cell.Z == at.Z);
            var mined = row.Definition is null;
            return (Wait.Signature(row.MineDesignated, row.Definition, wall?.Building?.Id), mined && wall is not null);
        }, cancellationToken).ConfigureAwait(false);
        s.RecordWire("ore_cell", row);
        s.RecordWire("wall", wall);
    }
}
