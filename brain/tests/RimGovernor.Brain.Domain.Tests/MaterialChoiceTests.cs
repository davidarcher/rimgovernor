using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class MaterialChoiceTests
{
    private static readonly Resource Metal = new("AMetal");
    private static readonly Resource Timber = new("BTimber");
    private static Fact<IReadOnlyDictionary<Resource, Fact<double>>> Budget(double metal = 10, double timber = 10) =>
        Known<IReadOnlyDictionary<Resource, Fact<double>>>(new Dictionary<Resource, Fact<double>> { [Metal] = Known(metal), [Timber] = Known(timber) });
    private static ProductionMaterialView View(double metal = 10, double timber = 10) => new(
        Known<EquatableArray<ProductionRecipeInput>>([
            new("FlexibleRecipe", [[new(Metal, 10), new(Timber, 10)]], false) { Products = [], WorkAmount = 0 },
            new("MetalRecipe", [[new(Metal, 10)]], false) { Products = [], WorkAmount = 0 }]), Budget(metal, timber));
    private static LedgerPlacement Flexible => new("bench-a", new() { Recipe = "FlexibleRecipe", BenchKind = "TestBench", Mode = BillMode.GearBatch, Target = 1 });
    private static LedgerPlacement Machine => new("bench-b", new() { Recipe = "MetalRecipe", BenchKind = "TestBench", Mode = BillMode.GearBatch, Target = 1 });
    private static ProductionBatch Batch(LedgerPlacement p, string id) => new(new ProductionBill(p.Bench, p.Spec.Recipe, p.Spec.Mode, p.Spec.Target, [.. p.Spec.Ingredients]),
        new ProgressView { Action = new(id), Stage = Stage.Pending });

    [Fact]
    public void JointChoiceFindsTimberFurnitureAndMetalMachineInsteadOfPinningTheLocalWinner()
    {
        var alone = LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible], View());
        Assert.Equal(Metal.Value, Assert.Single(Assert.Single(alone.Selected).Spec.Ingredients));
        var together = LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible, Machine], View());
        Assert.Equal(2, together.Selected.Count);
        Assert.Equal(Timber.Value, Assert.Single(Assert.Single(together.Selected, p => p.Spec.Recipe == Flexible.Spec.Recipe).Spec.Ingredients));
        Assert.Equal(Metal.Value, Assert.Single(Assert.Single(together.Selected, p => p.Spec.Recipe == Machine.Spec.Recipe).Spec.Ingredients));
        Assert.False(together.GenerationTruncated);
        Assert.False(together.Comparison.SearchTruncated);
        var reversed = LedgerMaterialChoicesRules.SelectLedgerMaterials([Machine, Flexible], View());
        Assert.Equal(together.Comparison.Selected.Select(s => s.Id), reversed.Comparison.Selected.Select(s => s.Id));
    }

    [Fact]
    public void SculptureDeclarationKeepsMaterialsAvailableForJointSelection()
    {
        var demand = new ArtDemand { Fits = 1, Stock = new Dictionary<Resource, long> { [Metal] = 10, [Timber] = 10 },
            Items = new ItemFacts {
                Sculptures = [new("FlexibleRecipe", "Sculpture", new Cell(1, 1), 10, 100)],
                Market = new Dictionary<Resource, double> { [Metal] = 20, [Timber] = 1 },
                StuffBeauty = new Dictionary<Resource, double> { [Metal] = 1, [Timber] = 1 },
                StuffCategories = new Dictionary<Resource, IReadOnlyList<string>> { [Metal] = ["Stuff"], [Timber] = ["Stuff"] },
                AcceptedStuff = new Dictionary<Resource, IReadOnlyList<string>> { [new("Sculpture")] = ["Stuff"] } } };
        var bench = new ProductionBench { Id = "bench-a", Usable = true, Token = "token",
            Recipes = [new() { Name = "FlexibleRecipe", Role = RecipeRole.Sculpture, Available = true, Food = new RecipeFood.NotFood() }] };
        var bill = Assert.Single(ArtRules.SelectArtBills([bench], 1L, [new RimGovernor.Brain.Domain.PawnId("artist")], demand));
        Assert.Equal(new[] { Metal.Value, Timber.Value }, bill.Ingredients);
        var declared = Flexible with { Spec = Flexible.Spec with { Ingredients = EquatableArray.From(bill.Ingredients), Worker = bill.Worker } };
        var selected = LedgerMaterialChoicesRules.SelectLedgerMaterials([declared, Machine], View());
        Assert.Equal(2, selected.Selected.Count);
        Assert.Equal(Timber.Value, Assert.Single(selected.Selected.Single(p => p.Spec.Worker == "artist").Spec.Ingredients));
    }

    [Fact]
    public void StockAndAllowedFiltersChangeTheCombinationAndCookingStillProceeds()
    {
        Assert.Single(LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible, Machine], View(10, 0)).Selected);
        Assert.Equal(2, LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible, Machine], View(20, 0)).Selected.Count);
        var pinned = Flexible with { Spec = Flexible.Spec with { Ingredients = [Metal.Value] } };
        Assert.Single(LedgerMaterialChoicesRules.SelectLedgerMaterials([pinned, Machine], View()).Selected);
        var cooking = new LedgerPlacement("cook", new() { Recipe = "TestFood", BenchKind = "TestCooker", Mode = BillMode.StockTarget, Target = 10 });
        var noMaterials = LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible, Machine, cooking], View(0, 0));
        Assert.Equal(cooking, Assert.Single(noMaterials.Selected));
    }

    [Fact]
    public void SuppliedValueChangesWhichCombinationWinsAndAValuelessOptionIsPostponed()
    {
        ForecastMaterialOption[] Options(double machineValue) => [
            new("small-a", "a", 4, [new(Metal, 5)]), new("small-b", "b", 4, [new(Metal, 5)]),
            new("machine", "m", machineValue, [new(Metal, 10)])];
        Assert.Equal(2, ForecastMaterialChoices.CompareMaterialPlans(Options(7), Budget()).Selected.Count);
        Assert.Equal("m", Assert.Single(ForecastMaterialChoices.CompareMaterialPlans(Options(9), Budget()).Selected).Id);
        Assert.Empty(ForecastMaterialChoices.CompareMaterialPlans([new("d", "valueless", 0, [new(Metal, 1)])], Budget()).Selected);
        Assert.Throws<DomainException>(() => ForecastMaterialChoices.CompareMaterialPlans([new("d", "negative", -1, [new(Metal, 1)])], Budget()));
    }

    [Fact]
    public void GlobalNativeFilterCannotPretendToPinOverlappingSlots()
    {
        var view = View() with { Recipes = Known<EquatableArray<ProductionRecipeInput>>([
            new("FlexibleRecipe", [[new(Metal, 5), new(Timber, 5)], [new(Metal, 5)]], false) { Products = [], WorkAmount = 0 }]) };
        // Choosing timber for slot one and metal for slot two reopens both choices in slot one.
        var choice = LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible], view with { Availability = Budget(5, 5) });
        Assert.Empty(choice.Selected);
        Assert.Contains(choice.Comparison.Findings, f => f.StartsWith("unrepresentable_or_unknown_filter:", StringComparison.Ordinal));
        var enough = LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible], view);
        Assert.Equal(Metal.Value, Assert.Single(Assert.Single(enough.Selected).Spec.Ingredients));
        Assert.Equal(10, Assert.Single(Assert.Single(enough.Comparison.Selected).Cost).Count);
    }

    [Fact]
    public void UnknownAndBoundedSearchKeepPostponementAndExposeTheirLimits()
    {
        var unknown = LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible], View() with { Recipes = default });
        Assert.Empty(unknown.Selected);
        Assert.Contains(unknown.Comparison.Findings, f => f.StartsWith("unknown_recipe:", StringComparison.Ordinal));
        var generated = LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible, Machine], View(), generationBudget: 1);
        Assert.True(generated.GenerationTruncated);
        Assert.Equal(1, generated.Generated);
        Assert.Empty(generated.Selected);
        var searched = LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible, Machine], View(), searchBudget: 1);
        Assert.True(searched.Comparison.SearchTruncated);
        Assert.Equal(1, searched.Comparison.Expansions);
        Assert.Single(searched.Selected);
        Assert.Empty(LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible], View() with { Availability = default }).Selected);
    }

    [Fact]
    public void PinnedReadbackSatisfiesBroadDemandWithoutDuplicateOrOrphanRemoval()
    {
        var selection = LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible, Machine], View());
        var actual = selection.Selected.Select((p, i) => new ActualBill { Id = "bill-" + i, Bench = p.Bench, Spec = p.Spec }).ToArray();
        var declared = new[] { new Declared { Orders = [Flexible.Spec, Machine.Spec] } };
        var plan = WorkRules.ReconcileLedger(declared, actual, actual.ToDictionary(b => b.Id, _ => WorkRules.OrphanGraceRounds), new Dictionary<string, int>());
        Assert.Empty(plan.Place);
        Assert.Empty(plan.Remove);
        Assert.Empty(plan.Orphans);
        Assert.False(WorkRules.IsMaterialSpecialization(Flexible.Spec with { Ingredients = [Metal.Value] }, actual.Single(b => b.Spec.Recipe == Flexible.Spec.Recipe).Spec));
        Assert.False(WorkRules.IsMaterialSpecialization(Flexible.Spec, actual[0].Spec with { Worker = "different-worker" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActivityBillAnchorIsNeitherStandingDemandNorAnOrphan(bool declared)
    {
        var anchor = new ActualBill { Id = "private-execution", Bench = "tailoring", Spec = Flexible.Spec, Kind = LedgerBillKind.ActivityExecution };
        var ordinary = anchor with { Id = "ordinary-orphan", Spec = Machine.Spec, Kind = LedgerBillKind.Production };
        var plan = WorkRules.ReconcileLedger(declared ? [new Declared { Orders = [Flexible.Spec] }] : [],
            [anchor, ordinary], new Dictionary<string, int> { [anchor.Id] = WorkRules.OrphanGraceRounds, [ordinary.Id] = WorkRules.OrphanGraceRounds }, new Dictionary<string, int>());
        Assert.Equal(ordinary, Assert.Single(plan.Remove));
        Assert.Empty(plan.Orphans);
        Assert.Equal(declared ? 1 : 0, plan.Place.Count);
    }
    [Fact]
    public void SelectedFiltersCommitTogetherAndBecomeExactSharedJournalClaims()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-material-options-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var world = new GenerationSnapshot(new ColonyId("colony"), new MapId(1), new LoadId("load"), new PlanId("review"), new PlanRevision(1), default);
        var facts = new RoundsFacts { Tick = new Tick(100),
            ForecastObserved = new() { WorkPawns = new WorkPawnsRead.Read(default(EquatableArray<WorkPawn>)) },
            WorkResources = Known(default(EquatableArray<RimGovernor.Contracts.Wire.Observations.WorkResourcePawn>)),
            Resources = [new(Metal, 10), new(Timber, 10)],
            CurrentConstruction = CurrentConstruction.Empty with { Deficit = new Dictionary<Resource, long>() },
            BillReservations = [],
            WorkAdmission = new() { Recipes = View().Recipes, Bills = Known(default(EquatableArray<ProductionBatchState>)) } };
        var review = journal.ReviewRounds(new() { Current = world, Tick = facts.Tick, Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        Assert.True(review.Review.Enabled);
        var selected = LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible, Machine], journal.ProductionMaterials(world, ct));
        var batches = selected.Selected.Select((p, i) => Batch(p, "action-" + i)).ToArray();
        var spec = new PlanSpec(new PlanId("joint"), new PlanRevision(1), batches.Select(b => DomainAction.NewProductionBillAction(b.Progress.Action, b.Order)));
        journal.CommitClaimedMethod(AdminFamily.WorkLedger, world, new MethodId("joint"), "", spec, ct);
        var committed = journal.ProductionBatches(world, facts.Tick, ct);
        Assert.Equal(2, committed.Value.Count);
        var claims = ProductionRules.ProductionPromisesOf(ProductionRules.PriceProductionBatches(committed, View().Recipes), default).Value.SelectMany(p => p.MaterialClaims).ToArray();
        Assert.Equal(10, Assert.Single(claims, c => c.Resource == Metal).Units.Value);
        Assert.Equal(10, Assert.Single(claims, c => c.Resource == Timber).Units.Value);
        Assert.Empty(LedgerMaterialChoicesRules.SelectLedgerMaterials([Flexible], journal.ProductionMaterials(world, ct)).Selected);
    }
}
