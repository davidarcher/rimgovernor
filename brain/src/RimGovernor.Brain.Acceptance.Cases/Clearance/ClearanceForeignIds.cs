using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>clearance/recovery-foreign-ids</c> (#2293): the recovery planners address foreign things by the id they rebuild
/// from the cell mirror (<see cref="Thing.LoadId"/>, <c>Thing_&lt;Def&gt;&lt;n&gt;</c>), and native must resolve that id
/// through <c>RefIndex.Thing</c>. A staged unowned Battery is claimed and a staged wild bush designated for cutting by
/// the rebuilt ids; the battery then reads as the colony's and the bush as designated.
/// </summary>
internal static class ClearanceForeignIds
{
    private const string Ruin = "Battery";
    private const string Bush = "Plant_Bush";

    internal static TypedCase Case() => new()
    {
        Name = "clearance/recovery-foreign-ids",
        Scope = "A staged unowned Battery and a wild bush: the ids rebuilt as Thing_<Def><n> equal native's, a building claim and "
            + "a CutPlant designation applied by them resolve through native RefIndex.Thing, and the battery then reads the colony's "
            + "faction and the bush designated (#2293).",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Stage = new Fx.StageSpec
        {
            Paused = true,
            Site = new Fx.StageSite
            {
                Width = 6,
                Height = 4,
                Near = Fx.StageAnchor.Colonist,
                OffsetX = 8,
                Radius = 30,
                Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unfogged, Fx.StageGround.Unzoned],
            },
            Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 6, 4), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones] }],
            Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 6, 4), Terrain = "Soil" }],
            Buildings =
            [
                new Fx.StageBuilding { Name = "ruin", Def = Ruin, Cells = [new(1, 1)], Faction = Fx.StageFaction.None },
                new Fx.StageBuilding { Name = "own", Def = "Wall", Stuff = "WoodLog", Cells = [new(5, 3)] },
            ],
            Plants = [new Fx.StagePlant { Name = "bush", Def = Bush, Cells = [new(4, 1)] }],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("stage: no site");
        var ruin = Rebuilt(staged.Buildings["ruin"].Ids.Single(), Ruin);
        var bush = Rebuilt(staged.Plants["bush"].Ids.Single(), Bush);
        var own = staged.Buildings["own"].Ids.Single();

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "foreign-claim", new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent
        {
            ThingId = ruin,
            Change = new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.ClaimCase(),
        }, cancellationToken).ConfigureAwait(false);
        _ = await TypedApply.AppliedAsync(s, "foreign-cut", new RimGovernor.Brain.GameOrders.Operations.DesignateIntent
        { Location = new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(),
            Designation = RimGovernor.Brain.GameOrders.Operations.ThingDesignation.CutPlant,
            Target = new() { Id = bush }, ReplaceWithWall = false, Withdraw = false, Guard = RimGovernor.Brain.GameOrders.Operations.DesignationGuard.NoGuard }, cancellationToken).ConfigureAwait(false);

        var rows = await s.BuildingsAsync("claimed", new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { Ids = [ruin, own], AnyFaction = true }, cancellationToken).ConfigureAwait(false);
        var faction = rows.FirstOrDefault(r => r.Building.Id == own)?.Faction?.Id;
        var claimed = rows.FirstOrDefault(r => r.Building.Id == ruin);
        if (faction is null || claimed?.Faction?.Id != faction)
        {
            throw new AcceptanceException($"claimed: want the battery in the colony's faction {faction}, got {claimed}");
        }

        var cell = staged.Plants["bush"].Cells.Single();
        var grid = await TypedApply.CellsAsync(s, "cut", site, cancellationToken).ConfigureAwait(false);
        var things = grid.ThingsAt(new Cell(cell.X, cell.Z));
        if (!things.Any(t => t.Def == Bush && t.Has(ThingMarks.Designated)))
        {
            throw new AcceptanceException($"cut: want the bush at {cell} designated, got [{string.Join("; ", things)}]");
        }
    }

    /// <summary>The id a planner rebuilds from the mirror's def and number, which must equal the id native reported.</summary>
    private static string Rebuilt(string native, string def)
    {
        var prefix = "Thing_" + def;
        if (!native.StartsWith(prefix, StringComparison.Ordinal) || !ulong.TryParse(native.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            throw new AcceptanceException($"thing id \"{native}\" is not Thing_{def}<n>");
        }

        var built = new Thing { Id = n, Def = def }.LoadId();
        return built == native ? built : throw new AcceptanceException($"rebuilt id \"{built}\" differs from native \"{native}\"");
    }
}
