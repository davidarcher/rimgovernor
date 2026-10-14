using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;


namespace RimGovernor.Brain.Domain.Tests;

public sealed class ClothingChoiceTests
{
    private static readonly Resource Cloth = new("Cloth"), Wool = new("Wool"), Shirt = new("Shirt");
    private static GearOption Garment(string id, Resource stuff, double cold, GearSource source = GearSource.Bill) => new() {
        ID = id, Definition = Shirt, Stuff = stuff, Slot = GearSlot.SkinTorso, Layers = ["OnSkin"], Groups = ["Torso"],
        Source = source, Quality = 2, Condition = 1, Cold = cold, Cost = 20, Sharp = 0.2 };
    private static GearPawn Pawn(string id, double ambient = 20, GearOption? worn = null) => new() {
        Pawn = new RimGovernor.Brain.Policy.PawnId(id), Loadout = "load-" + id, Candidates = Known(default(EquatableArray<GearCandidate>)),
        Apparel = Known(default(EquatableArray<GearApparel>)),
        LoadoutModel = Known(new GearLoadoutInput { Ambient = ambient, ComfortableMin = 10, ComfortableMax = 30,
            Worn = worn is null ? [] : [worn], Options = [Garment("cloth", Cloth, 5), Garment("wool", Wool, 15)] }) };
    private static GearRecipe Recipe => new() { Definition = "MakeShirt", Products = [Shirt], Available = Known(true), AvailableOn = Known(true),
        RequiredWork = Known(default(EquatableArray<WorkRequirement>)), WorkAmount = Known(100.0),
        Ingredients = Known<EquatableArray<EquatableArray<Amount>>>([[new(Cloth, 10), new(Wool, 10)]]) };
    private static GearBench Bench => new() { Id = "bench", Def = "Tailor", Usable = Known(true), WorkSpeed = Known(1.0),
        Recipes = Known<EquatableArray<GearRecipe>>([Recipe]), Bills = Known(default(EquatableArray<GearBill>)) };
    private static GearPlanningRequest Request(GearPawn[] pawns) => new() {
        Observation = Known(new GearObservation { Pawns = pawns, Stored = Known(default(EquatableArray<GearStock>)) }),
        Benches = Known<EquatableArray<GearBench>>([Bench]), MaterialPrices = new Dictionary<Resource, double> { [Cloth] = 1, [Wool] = 2 },
        StuffCategories = new Dictionary<Resource, IReadOnlyList<string>> { [Cloth] = ["Fabric"], [Wool] = ["Fabric"] } };
    private static ProductionMaterialView View(double cloth = 30, double wool = 30) => new(
        Known<EquatableArray<ProductionRecipeInput>>([new("MakeShirt", Recipe.Ingredients, Known(false)),
            new("Art", Known<EquatableArray<EquatableArray<Amount>>>([[new(Wool, 10)]]), Known(false))]),
        Known<IReadOnlyDictionary<Resource, Fact<double>>>(new Dictionary<Resource, Fact<double>> { [Cloth] = Known(cloth), [Wool] = Known(wool) }));
    private static LedgerPlacement[] Placements(Declared d) => d.Orders.Select((o, i) => new LedgerPlacement("bench-" + i, o)).ToArray();

    [Fact]
    public void ObservedColdSelectsWarmMaterialAndWarmWeatherPrefersCheaperCoverage()
    {
        var cold = DeclareGearOrders(Request([Pawn("pawn", 0)]));
        Assert.False(cold.Abstained());
        Assert.Equal(2, Assert.Single(cold.Orders).Requirements!.Variants.Count);
        var selected = SelectLedgerMaterials(Placements(cold), View());
        Assert.Equal(Wool.Value, Assert.Single(Assert.Single(selected.Selected).Spec.Ingredients));
        Assert.Equal(1, selected.Comparison.Priority.Safety);
        var warm = SelectLedgerMaterials(Placements(DeclareGearOrders(Request([Pawn("pawn")]))), View());
        Assert.Equal(Cloth.Value, Assert.Single(Assert.Single(warm.Selected).Spec.Ingredients));
        Assert.Equal(0, warm.Comparison.Priority.Safety);
        Assert.Equal(1, warm.Comparison.Priority.Required);
        Assert.Equal(10, warm.Comparison.Priority.MaterialCost);
        Assert.Equal(100, warm.Comparison.Priority.Work);
    }

    [Fact]
    public void RequiredClothingBeatsOptionalProductionAndScarcityChangesTheCombination()
    {
        var clothes = Placements(DeclareGearOrders(Request([Pawn("pawn", 0)])));
        var art = new LedgerPlacement("art", new() { Recipe = "Art", Mode = BillMode.GearBatch, Target = 1 });
        var choice = SelectLedgerMaterials([.. clothes, art], View(0, 10));
        Assert.Equal("MakeShirt", Assert.Single(choice.Selected).Spec.Recipe);
        Assert.Equal("Art", Assert.Single(choice.Postponed).Spec.Recipe);
        var partial = SelectLedgerMaterials(clothes, View(10, 0));
        Assert.Equal(Cloth.Value, Assert.Single(Assert.Single(partial.Selected).Spec.Ingredients));
        Assert.Equal(0.5, partial.Comparison.Priority.Safety);
    }

    [Fact]
    public void StoredAndQueuedGarmentsReduceTheSameShortageOnce()
    {
        var request = Request([Pawn("a"), Pawn("b"), Pawn("c")]);
        var chosenStuff = Assert.Single(DeclareGearOrders(request).Orders).Ingredients.Contains(Cloth.Value) ? Cloth : Wool;
        // Both material alternatives are present; the warm loadout's stable tie chooses cloth.
        request = request with { Observation = Known(request.Observation.Value with { Stored = Known<EquatableArray<GearStock>>([
            new() { Definition = Shirt, Stuff = chosenStuff, Quality = 2, HPBand = 9, Count = 1 }]) }) };
        var standing = new OrderSpec { BenchKind = "Tailor", Recipe = "MakeShirt", Mode = BillMode.GearBatch, Target = 1, Ingredients = [chosenStuff.Value] };
        request = request with { Benches = Known<EquatableArray<GearBench>>([Bench with { Bills = Known<EquatableArray<GearBill>>([
            new() { Id = "existing", Products = [Shirt], Active = Known(true), Spec = Known(standing) }]) }]) };
        var orders = DeclareGearOrders(request);
        Assert.False(orders.Abstained());
        Assert.Equal(2, orders.Orders.Count);
        Assert.Equal(standing, Assert.Single(orders.Orders, o => o.Requirements is null));
        Assert.Equal(1, Assert.Single(orders.Orders, o => o.Requirements is not null).Target);
        var residual = Assert.Single(orders.Orders, o => o.Requirements is not null);
        var extra = SelectLedgerMaterials([new("bench", residual)], View());
        Assert.Equal(Wool.Value, Assert.Single(Assert.Single(extra.Selected).Spec.Ingredients));
        var satisfied = request with { Observation = Known(request.Observation.Value with { Pawns = [Pawn("a"), Pawn("b")] }) };
        Assert.Null(Assert.Single(DeclareGearOrders(satisfied).Orders).Requirements);
    }

    [Fact]
    public void OptionalUpgradeWaitsAndUnknownWorkOrPriceCannotAuthorizeClothing()
    {
        var worn = Garment("worn", Cloth, 5, GearSource.Worn) with { Sharp = 0, Blunt = 0 };
        var upgrade = Pawn("pawn", worn: worn);
        upgrade = upgrade with { LoadoutModel = Known(upgrade.LoadoutModel.Value with { Options = [Garment("upgrade", Cloth, 5) with { Sharp = 5 }] }) };
        var optional = DeclareGearOrders(Request([upgrade]));
        Assert.Single(optional.Orders);
        Assert.Empty(SelectLedgerMaterials(Placements(optional), View()).Selected);
        var missing = Request([Pawn("pawn")]) with { MaterialPrices = new Dictionary<Resource, double>() };
        var unknown = SelectLedgerMaterials(Placements(DeclareGearOrders(missing)), View());
        Assert.Empty(unknown.Selected);
        Assert.Contains(unknown.Comparison.Findings, f => f.StartsWith("unknown_requirement_cost:", StringComparison.Ordinal));
        var work = Request([Pawn("pawn")]) with { Benches = Known<EquatableArray<GearBench>>([Bench with { Recipes = Known<EquatableArray<GearRecipe>>([Recipe with { WorkAmount = default }]) }]) };
        Assert.Empty(SelectLedgerMaterials(Placements(DeclareGearOrders(work)), View()).Selected);
        var unread = Request([Pawn("pawn")]);
        unread = unread with { Observation = Known(unread.Observation.Value with { Stored = default }) };
        Assert.True(DeclareGearOrders(unread).Abstained());
    }

    [Fact]
    public void PrioritiesAreLexicographicAndSharedNeedReliefIsCapped()
    {
        ForecastMaterialOption Needed(string id, string need, double cost, double work) =>
            new(id, id, Known(0.0), [new(Cloth, 10)]) { Requirements = new([new(need, ProductionNeedPriority.Required, 1)], Known(cost), Known(work)) };
        var optional = new ForecastMaterialOption("optional", "optional", Known(1e100), [new(Cloth, 20)]);
        var one = Needed("a", "coverage", 10, 100);
        var duplicate = Needed("b", "coverage", 20, 100);
        var distinct = Needed("c", "other-pawn", 10, 100);
        var result = CompareMaterialPlans([one, duplicate, distinct, optional], View(20, 0).Availability);
        Assert.Equal(["a", "c"], result.Selected.Select(o => o.Id));
        Assert.Equal(2, result.Priority.Required);
        var faster = one with { Id = "fast", Demand = one.Demand, Requirements = one.Requirements! with { Work = Known(50.0) } };
        Assert.Equal("fast", Assert.Single(CompareMaterialPlans([one, faster], View().Availability).Selected).Id);
        var tattered = Garment("old", Cloth, 5, GearSource.Worn) with { Condition = 0.4 };
        var maintenance = SelectLedgerMaterials(Placements(DeclareGearOrders(Request([Pawn("pawn", worn: tattered)]))), View());
        Assert.Equal(1, maintenance.Comparison.Priority.Maintenance);
    }

    [Fact]
    public void PhysicalReplacementPrecedesCraftingAndIndefiniteBillsDoNotInventOutput()
    {
        var pawn = Pawn("pawn");
        pawn = pawn with { LoadoutModel = Known(pawn.LoadoutModel.Value with {
            Options = [Garment("stored", Cloth, 5, GearSource.Stored), Garment("cloth", Cloth, 5)] }),
            Candidates = Known<EquatableArray<GearCandidate>>([new("stored", 100, Shirt)]) };
        Assert.Empty(DeclareGearOrders(Request([pawn])).Orders);
        var standing = new OrderSpec { BenchKind = "Tailor", Recipe = "MakeShirt", Mode = BillMode.StockTarget, Target = 1 };
        var request = Request([Pawn("a"), Pawn("b")]) with { Benches = Known<EquatableArray<GearBench>>([Bench with {
            Bills = Known<EquatableArray<GearBill>>([new() { Id = "bill", Products = [Shirt], Active = Known(true), Spec = Known(standing) }]) }]) };
        Assert.Equal(standing, Assert.Single(DeclareGearOrders(request).Orders));
    }
}
