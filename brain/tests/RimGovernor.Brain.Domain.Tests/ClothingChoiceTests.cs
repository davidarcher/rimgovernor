using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Store;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using Amount = RimGovernor.Brain.Policy.Amount;
using GearStock = RimGovernor.Brain.Policy.GearStock;
using GearCandidate = RimGovernor.Brain.Policy.GearCandidate;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ClothingChoiceTests
{
    private static GearOption Garment(string product, string stuff, double cold = 5, string group = "Torso") => new()
    {
        ID = product + stuff,
        Definition = new(product),
        Stuff = new(stuff),
        Slot = group == "Torso" ? GearSlot.SkinTorso : GearSlot.SkinLegs,
        Layers = ["OnSkin"],
        Groups = [group],
        Source = GearSource.Bill,
        Quality = 2,
        Condition = 1,
        Cold = cold,
        Cost = 20,
        Sharp = 0.2
    };
    private static GearObservation Census(double ambient = 20, params GearOption[] options) => new()
    {
        Stored = Known(default(EquatableArray<GearStock>)),
        Pawns = [new() { Pawn = new("wearer"), Loadout = "load",
        Candidates = Known(default(EquatableArray<GearCandidate>)), Apparel = Known(default(EquatableArray<GearApparel>)),
        LoadoutModel = Known(new GearLoadoutInput { Ambient = ambient, ComfortableMin = 10, ComfortableMax = 30,
            Options = options.Length == 0 ? [Garment("Shirt", "Cloth"), Garment("Shirt", "Wool", 15)] : EquatableArray.From(options) }) }]
    };
    private static RecipeIngredientObservation Input(string id, string stuff, int count = 10, double travel = 5) =>
        new(id, stuff, count, new(id == "a-far" ? 50 : id == "c-near" ? 6 : 5, 5), count) { Capacity = 75, ApproachTicks = Known(travel), CarryTicks = Known(5.0), ReturnTicks = Known(5.0) };
    private static RecipeActivityOption Option(string stuff, string product = "Shirt", double work = 100, string pawn = "tailor", string bench = "bench") =>
        new(product + stuff + pawn, pawn, bench, "Tailor", "Make" + product, product, stuff, "", "", new(10, 10),
            [Input(stuff, stuff)], Known(0.0), Known(work), Known(5.0), new(15, 15))
        { RequiredCount = 10 };
    private static RecipeActivityRead Read(params RecipeActivityOption[] options) => new()
    { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = EquatableArray.From(options) };
    private static RoundsFacts Facts(double cloth = 30, double wool = 30) => WorkResourceTestFacts.Create("tailor", "helper") with
    { Resources = Known<EquatableArray<Amount>>([new(new("Cloth"), (long)cloth), new(new("Wool"), (long)wool)]) };
    private static WorkResourceView Resources(double cloth = 30, double wool = 30) => WorkResourcesOf(
        WorkAdmissionEvidenceOf(Facts(cloth, wool), Known(default(EquatableArray<ProductionBatch>))), Known(default(EquatableArray<ProductionBatch>)), []);
    private static PawnActivities? Start(GearObservation census, RecipeActivityRead read, WorkResourceView resources,
        out ForecastActivityOutcomeComparison? comparison)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0);
        var template = facts.ForecastObserved.Needs!.Pawns[0];
        facts = facts with { Gear = Known(census), WorkView = resources, ForecastObserved = facts.ForecastObserved with
        {
            Needs = facts.ForecastObserved.Needs with { Pawns = [template with { Pawn = "tailor" }, template with { Pawn = "helper" }] },
            Coupled = new() { Tick = 100, Clothing = read }
        } };
        var generated = GenerateCoupledActivities(facts, 1900)!;
        var request = CoupledForecastRequestOf(new("world/map/load", 100), 1900d / GameTime.TicksPerDay,
            facts, new(), new() { Work = ForecastWorkInputsOf(facts) }, [], new());
        var selected = CompareActivityOutcomes(request, generated);
        comparison = selected.Selection;
        return StartCoupledActivity(selected, generated, 1, "clothing", 100);
    }
    private static PawnActivityRecipe Recipe(PawnActivities batch) => batch.Tasks.Single(t => t.Step.Recipe != null).Step.Recipe!;

    [Fact]
    public void OnlyCommittedConstructionScarcityChangesStuffWithoutPinningIt()
    {
        var census = Census(0); var read = Read(Option("Cloth"), Option("Wool"));
        Assert.Equal("Wool", Recipe(Start(census, read, Resources(), out _)!).Stuff);
        var facts = Facts() with { ConstructionDeficit = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [new("Wool")] = 30 }) };
        var resources = WorkResourcesOf(WorkAdmissionEvidenceOf(facts, Known(default(EquatableArray<ProductionBatch>))), Known(default(EquatableArray<ProductionBatch>)), []);
        Assert.Equal("Wool", Recipe(Start(census, read, resources, out _)!).Stuff); // A standing site is optional.
        var evidence = WorkAdmissionEvidenceOf(Facts(), Known(default(EquatableArray<ProductionBatch>))) with
        { Buildings = Known<EquatableArray<WorkBuildingCost>>([new("TestWall", "Wool", Known<EquatableArray<Amount>>([new(new("Wool"), 30)]))]) };
        var wall = DomainAction.NewBuildingAction(new("competing-wall"), new("TestWall", new(3, 3), Rotation.North, "Wool"), ConstructionTier.Survive);
        var committed = WorkResourcesOf(evidence, Known(default(EquatableArray<ProductionBatch>)),
            [new(new("construction-method"), wall, new() { Action = wall.Id, Stage = Stage.Pending })]);
        Assert.Equal("Cloth", Recipe(Start(census, read, committed, out _)!).Stuff);
        Assert.Null(Start(census, read, Resources(0, 0), out _));
    }

    [Fact]
    public void GarmentShapeRemainsOpenUntilNeedsAndWorkAreCompared()
    {
        var options = new[] { Garment("Shirt", "Cloth", 2), Garment("Parka", "Cloth", 20) };
        var read = Read(Option("Cloth", "Shirt", 20), Option("Cloth", "Parka", 200));
        Assert.Equal("MakeParka", Recipe(Start(Census(0, options), read, Resources(), out _)!).Recipe);
        Assert.Equal("MakeShirt", Recipe(Start(Census(20, options), read, Resources(), out _)!).Recipe);
    }

    [Fact]
    public void IngredientStackAlternativesIncludeSplitInputsAndTravelChangesChoice()
    {
        var row = Option("Cloth") with { Inputs = [Input("a-far", "Cloth", 10, 300), Input("b-near", "Cloth", 6), Input("c-near", "Cloth", 4)] };
        var selected = Recipe(Start(Census(), Read(row), Resources(), out _)!);
        Assert.Equal(["b-near", "c-near"], selected.Ingredients.Select(i => i.Thing).Order().ToArray());
        Assert.Equal(10, selected.Ingredients.Sum(i => i.Count));
        row = row with { Inputs = [Input("a-far", "Cloth", 10, 1), Input("b-near", "Cloth", 6, 300), Input("c-near", "Cloth", 4, 300)] };
        Assert.Equal("a-far", Assert.Single(Recipe(Start(Census(), Read(row), Resources(), out _)!).Ingredients).Thing);
    }

    [Fact]
    public void RetainingObservedCurrentProductAndPostponingAreRealComparisonResults()
    {
        var read = Read(Option("Cloth")) with { Retained = [new("running-uft", "Shirt", "Cloth", 1, Known(25.0))] };
        Assert.Null(Start(Census(), read, Resources(), out var retained));
        Assert.StartsWith("retain-", Assert.Single(retained!.Selected));
        Assert.Null(Start(Census(), Read(Option("Cloth", work: 3000)), Resources(), out var postpone));
        Assert.Empty(postpone!.Selected);
        Assert.Null(Start(Census(), Read(Option("Cloth") with { WorkTicks = default }), Resources(), out _));
    }

    [Fact]
    public void UnfinishedWorkUsesRemainingWorkAndDoesNotSpendLooseIngredientsAgain()
    {
        var unfinished = Option("Cloth") with { Bill = "owned-bill", Unfinished = "unfinished-shirt", Inputs = [], WorkTicks = Known(10.0) };
        var result = Start(Census(), Read(unfinished), Resources(0, 0), out _)!;
        Assert.Equal("unfinished-shirt", Recipe(result).Unfinished);
        Assert.Empty(Recipe(result).Ingredients);
        Assert.Equal("Cloth", result.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Deliver).ProductStuff);
    }

    [Fact]
    public void ExistingExactBillCanExecuteItsAlreadyHeldInputsButCannotFundAnotherRecipe()
    {
        // Stock is physical total; a reservation removes availability, not the actual items.
        // The former local selector incorrectly accepted a promise even with zero physical stock.
        var empty = Resources(0, 0).Evidence with
        { Promises = Known<EquatableArray<ProductionMaterialPromise>>([new("existing", [new(new("Cloth"), Known(10.0), Known("standing-bill"))])]) };
        Assert.Null(Start(Census(), Read(Option("Cloth") with { Bill = "standing-bill" }),
            WorkResourcesOf(empty, Known(default(EquatableArray<ProductionBatch>)), []), out _));
        var evidence = Resources(10, 0).Evidence with { Promises = empty.Promises };
        var resources = WorkResourcesOf(evidence, Known(default(EquatableArray<ProductionBatch>)), []);
        var selected = Start(Census(), Read(Option("Cloth") with { Bill = "standing-bill" }), resources, out _)!;
        Assert.Equal("standing-bill", Recipe(selected).Bill);
        Assert.Null(Start(Census(), Read(Option("Cloth")), resources, out _));
        evidence = Resources(100, 0).Evidence with
        { Promises = Known<EquatableArray<ProductionMaterialPromise>>([new("existing", [new(new("Cloth"), Known(100.0), Known("standing-bill"))])]) };
        resources = WorkResourcesOf(evidence, Known(default(EquatableArray<ProductionBatch>)), []);
        var other = Option("Cloth", "Pants", pawn: "helper", bench: "other-bench") with
        { Inputs = [Input("other-stack", "Cloth")], Destination = new(16, 15) };
        var bundle = Start(Census(20, Garment("Shirt", "Cloth"), Garment("Pants", "Cloth", group: "Legs")),
            Read(Option("Cloth") with { Bill = "standing-bill" }, other), resources, out _)!;
        Assert.Single(bundle.Tasks, t => t.Step.Recipe != null);
    }

    [Fact]
    public void ComplementaryGarmentsCanShareAStackButCannotDoubleSpendItOrTheirDestination()
    {
        var census = Census(20, Garment("Shirt", "Cloth"), Garment("Pants", "Cloth", group: "Legs"));
        var shirt = Option("Cloth") with { Inputs = [Input("shared", "Cloth", 20)] };
        var pants = Option("Cloth", "Pants", pawn: "helper", bench: "other-bench") with
        { Inputs = [Input("shared", "Cloth", 20)], Destination = new(16, 15) };
        var together = Start(census, Read(shirt, pants), Resources(), out _)!;
        Assert.Equal(2, together.Tasks.Count(t => t.Step.Recipe != null));
        Assert.Equal(20, together.Tasks.Where(t => t.Step.Recipe != null).Sum(t => t.Step.Recipe!.Ingredients.Sum(i => i.Count)));
        var tooSmall = Read(shirt with { Inputs = [Input("shared", "Cloth", 15)] }, pants with { Inputs = [Input("shared", "Cloth", 15)] });
        Assert.Single(Start(census, tooSmall, Resources(), out _)!.Tasks, t => t.Step.Recipe != null);
        Assert.Single(Start(census, Read(shirt, pants with { Destination = shirt.Destination }), Resources(), out _)!.Tasks, t => t.Step.Recipe != null);
    }

    [Fact]
    public void RetainedGarmentCannotAddProtectionAlongsideAConflictingReplacement()
    {
        var census = Census(0, Garment("Shirt", "Cloth", 5), Garment("Parka", "Cloth", 10));
        var read = Read(Option("Cloth", "Parka")) with { Retained = [new("ongoing", "Shirt", "Cloth", 1, Known(10.0))] };
        Start(census, read, Resources(), out var comparison);
        Assert.Single(comparison!.Selected);
    }

    [Fact]
    public void UnknownsPhysicalReplacementsAndGenerationBoundsRemainVisible()
    {
        var census = Census();
        Assert.Contains("unknown_clothing_stock", ClothingActivityRequirements(census with { Stored = default }).Findings);
        var physical = census with { Pawns = [census.Pawns[0] with { Candidates = Known<EquatableArray<GearCandidate>>([new("stored-shirt", 1, new("Shirt"))]) }] };
        Assert.Empty(ClothingActivityRequirements(physical).Alternatives);
        Start(census, Read(Option("Cloth")) with { Truncated = true }, Resources(), out var bounded);
        Assert.Contains("coupled_generation_truncated", bounded!.Findings);
    }

    [Fact]
    public void NormalDemandComparisonJournalAndHandsPreserveExactRecipeAndObservedOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
        var path = Path.Combine(Path.GetTempPath(), "rg-clothing-" + Guid.NewGuid().ToString("N") + ".db");
        BridgeFunctions.RegisterIntentKinds();
        using var journal = Journal.Open(path, ct);
        var review = journal.ReviewRounds(new RoundsRequest
        {
            Current = world,
            Tick = new(100),
            Enabled = true,
            Policy = DefaultRoundsPolicy(),
            Facts = Facts() with { Gear = Known(Census()) }
        }, ct);
        var owner = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainEquipment);
        var batch = Start(Census(), Read(Option("Cloth")), journal.WorkResources(world, ct), out _)!;
        Assert.NotNull(batch);
        var plan = new PlanSpec(new("make-clothing"), new(1), [DomainAction.NewPawnActivitiesAction(new("recipe-action"), batch)]);
        journal.CommitMethod(owner.Standard.Id, owner.Revision, new("clothing"), plan, ct);
        var stored = journal.LoadPlan(plan.Id, ct).Spec.Actions[0];
        var intent = BridgeFunctions.IntentAction(stored.Id.Value, stored).Intent!.PawnActivities!.Batch!;
        Assert.Equal("Cloth", Recipe(intent).Stuff);
        Assert.Equal(batch.Tasks, intent.Tasks);
        var output = batch.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Deliver);
        var execution = new ActivityTaskRead
        {
            Known = true,
            Context = Read().Context! with { Tick = 400 },
            Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
                new(o.Assignment, o.Method, o.Revision, 0, "completed", "", null, 100)))),
            Bindings = [new(output.Id, output.Step with { Target = "actual-cloth-product" })]
        };
        var delivery = ContinueCoordinatedActivity(batch, Known(true), execution)!;
        Assert.Equal("actual-cloth-product", delivery.Orders.Single().Steps[0].Target);
        Assert.Null(delivery.Orders.Single().Steps[0].Recipe);
        var failed = execution with { Pawns = EquatableArray.From(execution.Pawns.Select(p => p with { Execution = p.Execution! with { State = "failed" } })) };
        Assert.True(ContinueCoordinatedActivity(batch, Known(true), failed)!.Orders.Single().Release);
        Assert.True(ContinueCoordinatedActivity(batch, Known(false), execution)!.Orders.Single().Release);
    }
}
