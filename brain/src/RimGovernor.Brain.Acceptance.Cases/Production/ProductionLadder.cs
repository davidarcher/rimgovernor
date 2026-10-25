using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/ladder</c>: the production ladder's last rung (the bill, docs/developers/architecture/facilities.md). With
/// Fabrication finished, a powered fabrication bench standing, steel in a stockpile and a colonist on Crafting, a standing
/// blueprint that owes components gives the economy/resource Need a component deficit; it dispatches the bill and the
/// colony's component count rises. The research, power and bench rungs are staged, not played.
/// </summary>
internal static class ProductionLadder
{
    private const string Component = "ComponentIndustrial";

    internal static TypedCase Case() => new()
    {
        Name = "production/ladder",
        Scope = "The ladder's bill rung: over a staged powered FabricationBench (Fabrication finished), a steel stockpile and a "
            + "WoodFiredGenerator blueprint owing two components, the served economy/resource Need dispatches the component bill "
            + "and observations_list_supplies reads the colony's ComponentIndustrial count above its staged zero. Construction "
            + "is off for every colonist so the blueprint cannot take the product; only the resource family is served.",
        Ticks = TypedCase.StagedTickCap,
        Tier = CaseTier.Nightly,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Research = [new Fx.StageResearch { Def = "Fabrication" }],
            Site = new Fx.StageSite
            {
                Width = 14,
                Height = 10,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 4,
                Radius = 40,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
            },
            Buildings =
            [
                new Fx.StageBuilding { Name = "bench", Def = "FabricationBench", Cells = [new(4, 5)] },
                new Fx.StageBuilding { Name = "generator", Def = "WoodFiredGenerator", Cells = [new(10, 6)], Fuel = 75 },
                new Fx.StageBuilding { Name = "conduit", Def = "HiddenConduit", Cells = [.. Enumerable.Range(1, 12).Select(x => new Fx.StageCell(x, 8))] },
                new Fx.StageBuilding { Name = "demand", Def = "WoodFiredGenerator", Cells = [new(10, 2)], State = Fx.StageBuildState.Blueprint },
            ],
            Things =
            [
                new Fx.StageThing { Name = "steel-1", Def = "Steel", Count = 75, Cell = new(2, 1) },
                new Fx.StageThing { Name = "steel-2", Def = "Steel", Count = 75, Cell = new(3, 1) },
                new Fx.StageThing { Name = "steel-3", Def = "Steel", Count = 75, Cell = new(2, 2) },
                new Fx.StageThing { Name = "steel-4", Def = "Steel", Count = 75, Cell = new(3, 2) },
            ],
            Pawns =
            [
                new Fx.StagePawn
                {
                    Name = "crafter",
                    Colonist = 0,
                    Cell = new(6, 3),
                    AllWork = 0,
                    Work = new Dictionary<string, int> { ["Crafting"] = 1 },
                    Timetable = Fx.StageTimeAssignment.Work,
                },
                new Fx.StagePawn { Name = "idle-1", Colonist = 1, AllWork = 0 },
                new Fx.StagePawn { Name = "idle-2", Colonist = 2, AllWork = 0 },
            ],
            Zones =
            [
                new Fx.StageZone
                {
                    Name = "steel",
                    Kind = Fx.StageZoneKind.Stockpile,
                    Cells = [new(2, 1), new(3, 1), new(2, 2), new(3, 2)],
                    Allow = ["Steel"],
                },
            ],
        },
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["resource"], NativeTimeout = TimeSpan.FromSeconds(15), Prefix = "production-ladder" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var before = await CountAsync(s, "before", cancellationToken).ConfigureAwait(false);
        s.Record("components_before", before);
        if (before != 0)
        {
            throw new AcceptanceException($"before: the staged colony holds {before} {Component}; the blueprint's deficit needs none");
        }

        var after = before;
        _ = await s.ServeUntilAsync("component-made", s.TicksLeft, async ct =>
        {
            after = await CountAsync(s, "after", ct).ConfigureAwait(false);
            return (Wait.Signature(after), after > before);
        }, cancellationToken).ConfigureAwait(false);
        s.Record("components_after", after);
    }

    // The colony's ComponentIndustrial units, held stock included (a crafter carrying the product to storage).
    private static async Task<long> CountAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var supplies = await s.CallAsync(
            label,
            NativeOps.ListSupplies,
            new RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest { Scope = s.Scope, Filter = new RimGovernor.Brain.GameFacts.Observations.StockFilter { DefNames = [Component], Category = RimGovernor.Brain.GameFacts.Observations.StockCategory.Haulable, Ownership = RimGovernor.Brain.GameFacts.Observations.StockOwnership.Ours, IncludeHeld = true } },
            cancellationToken).ConfigureAwait(false);
        return supplies.Stocks.Where(row => row.DefName == Component).Sum(row => row.Units);
    }
}
