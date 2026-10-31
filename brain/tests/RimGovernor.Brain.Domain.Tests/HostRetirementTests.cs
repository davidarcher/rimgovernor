using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 3.2 (RG-HABITAT-005): a standing spare's fate is decided only once its replacement is usable, and its removal never
/// interrupts work. A placed, unbuilt, unpowered or bill-less replacement retires nothing; a spare a pawn is working at is
/// held naming the bill, then retired once the work is done; a spare the plan keeps, a load still named for it and another
/// owner's claim keep it. A shared spare takes the replacement's dispatcher-sized orders.
/// </summary>
public sealed class HostRetirementTests
{
    private const string Spot = "CraftingSpot", Smithy = "Smithy", Knife = "MakeKnife";
    private static readonly OrderSpec KnifeOrder = new() { Recipe = Knife, Target = 1, Mode = BillMode.StockTarget };
    private static readonly HostBench SpotBench = new("spot", Spot, true, [Knife], []), SmithyBench = new("smithy", Smithy, true, [Knife], []);

    private static LayoutPlan Plan(params PlannedProduction[] made) => new() { Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Workshop, Production = [.. made] }] };

    private static readonly LayoutPlan Replaced = Plan(new PlannedProduction(Smithy, Knife, 1));

    private static LedgerView View(OrderSpec[] orders, params HostBench[] benches) => WorkRules.NewLedgerView(WorkRules.LedgerViewReconciled) with { Orders = orders, Benches = benches };

    private static IReadOnlyList<HostRetirement> Of(LayoutPlan plan, LedgerView view, Func<string, bool>? claimed = null) =>
        PlannedRules.HostRetirementsOf(plan, view, [Knife], claimed ?? (_ => false));

    [Fact]
    public void ASpareRetiresOnlyOnceItsReplacementServes()
    {
        Assert.Equal([new HostRetirement.Retire("spot", Spot)], Of(Replaced, View([KnifeOrder], SpotBench, SmithyBench)));

        // Placed or unbuilt: no standing smithy. Unpowered or unreachable: it does not serve. Bill-less: it offers no knife, or no order asks one.
        Assert.Empty(Of(Replaced, View([KnifeOrder], SpotBench)));
        Assert.Empty(Of(Replaced, View([KnifeOrder], SpotBench, SmithyBench with { Serves = false })));
        Assert.Empty(Of(Replaced, View([KnifeOrder], SpotBench, SmithyBench with { Recipes = [] })));
        Assert.Empty(Of(Replaced, View([], SpotBench, SmithyBench)));
        Assert.Empty(Of(Replaced, View([KnifeOrder with { Target = 0 }], SpotBench, SmithyBench)));
    }

    [Fact]
    public void ABillInProgressHoldsTheRemovalNamingItThenTheSpareRetires()
    {
        var working = View([KnifeOrder], SpotBench with { Working = ["bill7"] }, SmithyBench);
        Assert.Equal([new HostRetirement.Held("spot", "bill in progress:bill7")], Of(Replaced, working));
        Assert.Equal([new HostRetirement.Retire("spot", Spot)], Of(Replaced, working with { Benches = [SpotBench, SmithyBench] }));
    }

    [Fact]
    public void AKeptSpareAPendingBridgeAStillNamedLoadAndAnotherOwnersClaimKeepTheSpare()
    {
        var view = View([KnifeOrder], SpotBench, SmithyBench);
        Assert.Empty(Of(Plan(new(Smithy, Knife, 1), new(Spot, Knife, 0) { Step = HostStep.Share }), view));
        Assert.Empty(Of(Plan(new(Smithy, Knife, 1), new(Spot, Knife, 1) { Step = HostStep.Bridge }), view));
        Assert.Empty(Of(Replaced, view, id => id == "spot"));
        // An order of another recipe still named for the spot runs there: the spot serves it.
        Assert.Empty(Of(Replaced, view with { Orders = [KnifeOrder, new OrderSpec { Recipe = "MakeClub", Target = 1, BenchKind = Spot }] }));
        // An order of the knife itself named for the spot is moved to the smithy by the plan, so it no longer keeps the spot.
        Assert.Single(Of(Replaced, view with { Orders = [KnifeOrder with { BenchKind = Spot }] }));
    }

    [Fact]
    public void ASharedBenchTakesOnlyTheReplacementsDispatcherSizedOrders()
    {
        var smithy = new GearBench { Observed = ProductionBenchFixtures.Observed("smithy", Smithy, "token", true), Bills = [], Recipes = [] };
        var stocked = KnifeOrder with { Product = new Resource("Knife"), BenchKind = Spot };
        Assert.False(smithy.Takes(stocked));
        Assert.True(smithy.Takes(stocked with { BenchKind = Smithy }));

        var pair = Plan(new(Smithy, Knife, 1), new(Spot, Knife, 1) { Step = HostStep.Share });
        var shared = Assert.Single(WorkRules.ShareBenches([smithy], pair, [stocked]));
        Assert.Equal([new BenchShare(Knife, Spot)], shared.Shares);
        Assert.True(shared.Takes(stocked));
        Assert.False(shared.Takes(stocked with { Mode = BillMode.ButcherForever }));
        // A plan hosting the knife at one definition shares nothing.
        Assert.Same(smithy, Assert.Single(WorkRules.ShareBenches([smithy], Replaced, [stocked])));
    }

    /// <summary>
    /// Task 3.2: the room reconcile removes a chain's bridge through the same rule. A spot the adopted plan no longer
    /// places is never cut while a pawn works its bill or before the smithy serves the knife; once the bill list is empty
    /// and the smithy serves, it is. The room's other work and other removals go on.
    /// </summary>
    [Fact]
    public void TheRoomReconcileNeverCutsABridgeBeforeItsReplacementServesOrWhileItsBillIsWorked()
    {
        var cut = new Operation { Kind = OpKind.FurnitureOut, Targets = [new ClearanceTarget { EntityId = "spot", DefName = Spot }] };
        var chair = new Operation { Kind = OpKind.FurnitureOut, Targets = [new ClearanceTarget { EntityId = "chair", DefName = "Stool" }] };
        var build = new Operation { Kind = OpKind.Build, Cells = [new(1, 1)] };
        IReadOnlyList<Operation> ops = [cut, chair, build];
        var view = View([KnifeOrder], SpotBench with { Working = ["bill7"] }, SmithyBench);

        var (working, fact) = PlannedRules.AdmittedRemovals(ops, Replaced, view);
        Assert.Equal([chair, build], working);
        Assert.Equal("spot:bill in progress:bill7", fact);
        var (unserved, waiting) = PlannedRules.AdmittedRemovals(ops, Replaced, view with { Benches = [SpotBench, SmithyBench with { Serves = false }] });
        Assert.Equal([chair, build], unserved);
        Assert.Equal("spot:replacement not serving:" + Knife, waiting);

        var (admitted, none) = PlannedRules.AdmittedRemovals(ops, Replaced, view with { Benches = [SpotBench, SmithyBench] });
        Assert.Same(ops[0], admitted[0]);
        Assert.Equal(ops, admitted);
        Assert.Null(none);
        // A bench the plan replaces nothing at is the reconcile's as before.
        Assert.Equal(ops, PlannedRules.AdmittedRemovals(ops, Plan(new PlannedProduction(Spot, Knife, 1)), view).Ops);
    }
}
