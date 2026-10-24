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
        LoadoutModel = Known(new GearLoadoutInput { Ambient = ambient, ComfortableMin = 10, ComfortableMax = 30, Worn = [Tattered],
            Options = options.Length == 0 ? [Garment("Shirt", "Cloth"), Garment("Shirt", "Wool", 15)] : EquatableArray.From(options) }) }]
    };
    // The wearer's tattered shirt and bare legs (#3816): a torso garment ends the tattered thought, pants the nakedness.
    private static GearOption Tattered => Garment("Shirt", "Cloth", 0) with
    { ID = "tattered-shirt", Source = GearSource.Worn, Sharp = 0, Condition = 0.1, CareIfDamaged = true, CountsAsClothingForNudity = true };
    private static NeedThoughtStage[] ApparelStages =>
    [
        new("ApparelDamaged", ForecastApparel.Damaged, 0, -3, Known((double)GameTime.TicksPerDay), false),
        new("ApparelDamaged", ForecastApparel.Damaged, 1, -5, Known((double)GameTime.TicksPerDay), false),
        new("Naked", ForecastApparel.Naked, 0, -6, Known((double)GameTime.TicksPerDay), false),
    ];
    private static RecipeIngredientObservation Input(string id, string stuff, int count = 10, double travel = 5) =>
        new(id, stuff, count, new(id == "a-far" ? 50 : id == "c-near" ? 6 : 5, 5), count) { Capacity = 75, ApproachTicks = Known(travel), CarryTicks = Known(5.0), ReturnTicks = Known(5.0) };
    private static RecipeActivityOption Option(string stuff, string product = "Shirt", double work = 100, string pawn = "tailor", string bench = "bench") =>
        new(product + stuff + pawn, pawn, bench, "Tailor", "Make" + product, product, stuff, "", "", new(10, 10),
            [Input(stuff, stuff)], Known(0.0), Known(work), Known(5.0), new(15, 15))
        { RequiredCount = 10 };
    private static RecipeActivityRead Read(params RecipeActivityOption[] options) => new()
    { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = EquatableArray.From(options) };
    private static RoundsFacts Facts(double cloth = 30, double wool = 30, double devilstrand = 0) => WorkResourceTestFacts.Create("tailor", "helper") with
    { Resources = Known<EquatableArray<Amount>>([new(new("Cloth"), (long)cloth), new(new("Wool"), (long)wool), new(new("DevilstrandCloth"), (long)devilstrand)]) };
    private static WorkResourceView Resources(double cloth = 30, double wool = 30, double devilstrand = 0) => WorkRules.WorkResourcesOf(
        ProductionRules.WorkAdmissionEvidenceOf(Facts(cloth, wool, devilstrand), Known(default(EquatableArray<ProductionBatch>))), Known(default(EquatableArray<ProductionBatch>)), []);
    private static RoundsFacts Review(GearObservation census, RecipeActivityRead read, WorkResourceView resources)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0);
        var template = facts.ForecastObserved.Needs!.Pawns[0];
        return facts with { Gear = Known(census), WorkView = resources, MoodBreak = new() { Threshold = 0.3, Tiers = [new(1, 2), new(1, 3), new(1, 5)] }, ForecastObserved = facts.ForecastObserved with
        {
            Needs = facts.ForecastObserved.Needs with { Pawns = [template with { Pawn = "tailor" }, template with { Pawn = "helper" },
                template with { Pawn = "wearer", Apparel = new(RimGovernor.Contracts.Wire.Defs.Gender.Male, true, true), ThoughtStages = [.. template.ThoughtStages, .. ApparelStages] }] },
            Coupled = new() { Research = ActivityContinuationFixtures.NoResearch, Hunt = HuntFixtures.NoHunt, Tick = 100, Clothing = read }
        } };
    }
    private static PawnActivities? Start(GearObservation census, RecipeActivityRead read, WorkResourceView resources,
        out ForecastActivityOutcomeComparison? comparison)
    {
        var facts = Review(census, read, resources);
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        // The colony model's horizon, as the review stamps it before the comparison (Rounder).
        generated = generated with { Context = generated.Context with { InvestmentHorizonDays = 10d } };
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 1900d / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        comparison = selected.Selection;
        return ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "clothing", 100);
    }
    private static PawnActivityRecipe Recipe(PawnActivities batch) => batch.Tasks.Single(t => t.Step.Recipe != null).Step.Recipe!;

    [Fact]
    public void OnlyCommittedConstructionScarcityChangesStuffWithoutPinningIt()
    {
        // Equal coverage and labor: the first stuff by id wins until committed construction takes it (#3330: the warmer wool
        // is no longer a thermal relief).
        var census = Census(0); var read = Read(Option("Cloth"), Option("Wool"));
        Assert.Equal("Cloth", Recipe(Start(census, read, Resources(), out _)!).Stuff);
        var facts = Facts() with { ConstructionDeficit = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [new("Cloth")] = 30 }) };
        var resources = WorkRules.WorkResourcesOf(ProductionRules.WorkAdmissionEvidenceOf(facts, Known(default(EquatableArray<ProductionBatch>))), Known(default(EquatableArray<ProductionBatch>)), []);
        Assert.Equal("Cloth", Recipe(Start(census, read, resources, out _)!).Stuff); // A standing site is optional.
        var evidence = ProductionRules.WorkAdmissionEvidenceOf(Facts(), Known(default(EquatableArray<ProductionBatch>))) with
        { Buildings = Known<EquatableArray<WorkBuildingCost>>([new("TestWall", "Cloth", Known<EquatableArray<Amount>>([new(new("Cloth"), 30)]))]) };
        var wall = DomainAction.NewBuildingAction(new("competing-wall"), new("TestWall", new(3, 3), Rotation.North, "Cloth"), ConstructionTier.Survive);
        var committed = WorkRules.WorkResourcesOf(evidence, Known(default(EquatableArray<ProductionBatch>)),
            [new(new("construction-method"), wall, new() { Action = wall.Id, Stage = Stage.Pending })]);
        Assert.Equal("Wool", Recipe(Start(census, read, committed, out _)!).Stuff);
        Assert.Null(Start(census, read, Resources(0, 0), out _));
    }

    [Fact]
    public void GarmentShapeRemainsOpenUntilNeedsAndWorkAreCompared()
    {
        // Both shapes relieve the same coverage. A cold ambient is no relief (#3330): with no forecast exposure to price the
        // parka's warmth, the cheaper shirt wins. ForecastCoupledSelectionTests.ColdParka covers the priced cold case.
        var options = new[] { Garment("Shirt", "Cloth", 2), Garment("Parka", "Cloth", 20) };
        var read = Read(Option("Cloth", "Shirt", 20), Option("Cloth", "Parka", 200));
        Assert.Equal("MakeShirt", Recipe(Start(Census(0, options), read, Resources(), out _)!).Recipe);
        Assert.Equal("MakeParka", Recipe(Start(Census(20, options), Read(Option("Cloth", "Shirt", 200), Option("Cloth", "Parka", 20)), Resources(), out _)!).Recipe);
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
        var read = Read(Option("Cloth")) with { Retained = [new("running-uft", "Shirt", "Cloth", Known(25.0))] };
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
            WorkRules.WorkResourcesOf(empty, Known(default(EquatableArray<ProductionBatch>)), []), out _));
        var evidence = Resources(10, 0).Evidence with { Promises = empty.Promises };
        var resources = WorkRules.WorkResourcesOf(evidence, Known(default(EquatableArray<ProductionBatch>)), []);
        var selected = Start(Census(), Read(Option("Cloth") with { Bill = "standing-bill" }), resources, out _)!;
        Assert.Equal("standing-bill", Recipe(selected).Bill);
        Assert.Null(Start(Census(), Read(Option("Cloth")), resources, out _));
        evidence = Resources(100, 0).Evidence with
        { Promises = Known<EquatableArray<ProductionMaterialPromise>>([new("existing", [new(new("Cloth"), Known(100.0), Known("standing-bill"))])]) };
        resources = WorkRules.WorkResourcesOf(evidence, Known(default(EquatableArray<ProductionBatch>)), []);
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
        var read = Read(Option("Cloth", "Parka")) with { Retained = [new("ongoing", "Shirt", "Cloth", Known(10.0))] };
        Start(census, read, Resources(), out var comparison);
        Assert.Single(comparison!.Selected);
    }

    [Fact]
    public void UnknownStockHidesRequirementsAndPhysicalReplacementsCompeteWithCrafting()
    {
        var census = Census();
        Assert.Empty(ClothingActivitiesRules.ClothingActivityRequirements(census with { Stored = default }));
        // #3621: a physical replacement no longer withholds the craft; the comparison weighs the swap against it.
        var physical = census with { Pawns = [census.Pawns[0] with { Candidates = Known<EquatableArray<GearCandidate>>([new("stored-shirt", 1, new("Shirt"))]) }] };
        Assert.Equal(ClothingActivitiesRules.ClothingActivityRequirements(census), ClothingActivitiesRules.ClothingActivityRequirements(physical));
        Assert.NotEmpty(ClothingActivitiesRules.ClothingActivityRequirements(physical));
    }

    private static GearOption Duster(string stuff, double sharp, GearSource source = GearSource.Bill) => Garment("Duster", stuff) with
    { ID = source == GearSource.Bill ? "Duster" + stuff : source + "-duster-" + stuff, Slot = GearSlot.Outer, Layers = ["Shell"], Source = source, Sharp = sharp, Cold = 0 };

    private static GearObservation Wearing(params GearOption[] options) => Census(20, options) with
    {
        Pawns = [Census(20, options).Pawns[0] with { LoadoutModel = Known(Census(20, options).Pawns[0].LoadoutModel.Value with
        { Worn = [Garment("Shirt", "Cloth", 0) with { ID = "worn-shirt", Source = GearSource.Worn, Sharp = 0 }, Duster("Cloth", 0.1, GearSource.Worn)] }) }]
    };

    [Fact]
    public void AnArmorOnlyCraftIsACandidateAndCarriesItsArmorAsAnOutfitChange()
    {
        // #3621: armor is priced by the harm term from the outfit change, so the value has one source.
        var need = Assert.Single(ClothingActivitiesRules.ClothingActivityRequirements(Wearing(Duster("DevilstrandCloth", 0.5))));
        Assert.True(need.Garment.Sharp > 0.1);
        Assert.Null(need.Comfort);
        Assert.Equal(0.5, need.Garment.Sharp);
        // The same garment as the worn one, armor and all, is no candidate.
        Assert.Empty(ClothingActivitiesRules.ClothingActivityRequirements(Wearing(Duster("Cloth", 0.1))));
    }

    [Fact]
    public void ASwapOfTheGarmentsSlotExcludesTheCraftAndASwapOfAnotherSlotDoesNot()
    {
        var need = Assert.Single(ClothingActivitiesRules.ClothingActivityRequirements(Wearing(Duster("DevilstrandCloth", 0.5))));
        var worn = Wearing().Pawns[0].LoadoutModel.Value.Worn;
        var dusterSwap = new ForecastCommitment("gear/wearer/a", []) { ExclusiveOutcome = "gear/wearer",
            OutfitChanges = [new("wearer", [worn[0], Duster("DevilstrandCloth", 0.5, GearSource.Stored)])] };
        var shirtSwap = new ForecastCommitment("gear/wearer/b", []) { ExclusiveOutcome = "gear/wearer",
            OutfitChanges = [new("wearer", [Garment("Shirt", "Wool") with { ID = "stored-shirt", Source = GearSource.Stored }, worn[1]])] };
        var other = new ForecastCommitment("gear/other/a", []) { ExclusiveOutcome = "gear/other", OutfitChanges = [new("other", [])] };

        Assert.Equal(["gear/wearer/a"], ClothingActivitiesRules.SwapsInSlot(need, worn, [dusterSwap, shirtSwap, other]));
        Assert.Equal(["gear/wearer/a", "gear/wearer/b"], ClothingActivitiesRules.SwapsInSlot(need, null, [dusterSwap, shirtSwap, other]));
    }

    [Fact]
    public void TheCoupledGeneratorExcludesTheStoredDusterSwapFromTheDusterCraft()
    {
        // #3621: one census, a stored devilstrand duster to swap in and a bill to craft one; the coupled generator joins them.
        var census = Wearing(Duster("DevilstrandCloth", 0.5, GearSource.Stored), Duster("DevilstrandCloth", 0.5));
        var facts = Review(census, Read(Option("DevilstrandCloth", "Duster")), Resources(devilstrand: 30)) with
        { MedicalPawns = Known<EquatableArray<CarePawn>>([new() { Id = new("wearer"), Conditions = Known<EquatableArray<CareCondition>>([]) }]) };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        var swaps = generated.Alternatives.Where(a => a.ExclusiveOutcome == "gear/wearer").ToArray();
        // Each ingredient allocation is its own craft alternative.
        var crafts = generated.Alternatives.Where(a => a.ExclusiveOutcome == "clothing/wearer/Duster").ToArray();
        var swap = Assert.Single(swaps, s => s.OutfitChanges.Single().Items.Any(i => i.ID == "Stored-duster-DevilstrandCloth"));
        Assert.NotEmpty(crafts);
        Assert.All(crafts, craft =>
        {
            Assert.Contains(swap.Id, craft.Outcome!.Excludes);
            Assert.Equal([new ForecastOutfitChange("wearer", [Duster("DevilstrandCloth", 0.5)]) { Overlay = true }], craft.OutfitChanges);
        });
    }

    [Fact]
    public void TheCapKeepsTheHighestScoredStuffsAndRecordsTheTruncation()
    {
        // #3621: seventeen shirt stuffs, armor rising with the index; the 16-outcome cap drops stuff 0, the weakest.
        var stuffs = Enumerable.Range(0, 17).Select(i => "Stuff" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var census = Census(20, [.. stuffs.Select((s, i) => Garment("Shirt", s) with { Sharp = 0.2 + 0.02 * i })]);
        var needs = ClothingActivitiesRules.ClothingActivityRequirements(census);
        Assert.Equal(Enumerable.Reverse(stuffs), needs.Select(n => n.Stuff));
        var read = Read([.. stuffs.Select(s => Option(s))]);
        var family = RimGovernor.Brain.Policy.Methods.Generation.ClothingMethodCandidates.Generate(read, needs, 2000, Resources());
        Assert.True(family.Truncated);
        Assert.Contains("clothing_generation_truncated", family.Findings);
        Assert.Equal(16, family.Outcomes.Length);
        Assert.DoesNotContain(family.Outcomes, o => o.Id.EndsWith("-Stuff00", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCapTakesStuffsRoundRobinSoTwoStuffsBothAppear()
    {
        // #3640: seventeen products in two stuffs, every devilstrand garment scored above every cloth one; before the round-robin
        // the devilstrand products alone filled the 16-outcome cap and cloth was never offered.
        var products = Enumerable.Range(0, 17).Select(i => "Product" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var census = Census(20, [.. products.SelectMany((p, i) => new[]
        {
            Garment(p, "Cloth") with { Sharp = 0.2 + 0.001 * i },
            Garment(p, "DevilstrandCloth") with { Sharp = 0.9 + 0.001 * i },
        })]);
        var needs = ClothingActivitiesRules.ClothingActivityRequirements(census);
        Assert.True(needs.Where(n => n.Stuff == "DevilstrandCloth").Min(n => n.Score) > needs.Where(n => n.Stuff == "Cloth").Max(n => n.Score));
        var read = Read([.. products.SelectMany(p => new[] { Option("Cloth", p), Option("DevilstrandCloth", p) })]);
        var family = RimGovernor.Brain.Policy.Methods.Generation.ClothingMethodCandidates.Generate(read, needs, 2000, Resources());
        Assert.True(family.Truncated);
        Assert.Equal(16, family.Outcomes.Length);
        Assert.Equal(8, family.Outcomes.Count(o => o.Id.EndsWith("-DevilstrandCloth", StringComparison.Ordinal)));
        Assert.Equal(8, family.Outcomes.Count(o => o.Id.EndsWith("-Cloth", StringComparison.Ordinal)));
        // Each stuff's best garment opens the first round, the higher-scored stuff first.
        Assert.EndsWith("-DevilstrandCloth", family.Outcomes[0].Id, StringComparison.Ordinal);
        Assert.EndsWith("-Cloth", family.Outcomes[1].Id, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalDemandComparisonJournalAndHandsPreserveExactRecipeAndObservedOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
        var path = Path.Combine(Path.GetTempPath(), "rg-clothing-" + Guid.NewGuid().ToString("N") + ".db");
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(path, ct);
        journal.ReviewRounds(new RoundsRequest
        {
            Current = world,
            Tick = new(100),
            Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(),
            Facts = Facts() with { Gear = Known(Census()) }
        }, ct);
        var batch = Start(Census(), Read(Option("Cloth")), journal.WorkResources(world, ct), out _)!;
        Assert.NotNull(batch);
        var plan = new PlanSpec(new("make-clothing"), new(1), [DomainAction.NewPawnActivitiesAction(new("recipe-action"), batch)]);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(MilitaryFamily.Equipment, world, new("clothing"), "", plan, ct).Outcome);
        var stored = journal.LoadPlan(plan.Id, ct).Spec.Actions[0];
        var intent = IntentActionRules.IntentAction(stored.Id.Value, stored).Intent!.PawnActivities!.Batch!;
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
        var delivery = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), execution)!;
        Assert.Equal("actual-cloth-product", delivery.Orders.Single().Steps[0].Target);
        Assert.Null(delivery.Orders.Single().Steps[0].Recipe);
        var failed = execution with { Pawns = EquatableArray.From(execution.Pawns.Select(p => p with { Execution = p.Execution! with { State = "failed" } })) };
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), failed)!.Orders.Single().Release);
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(false), execution)!.Orders.Single().Release);
    }
    [Fact]
    public void SelectedGeneratedTailoringLearnsDuringWorkButNotIngredientCarrying()
    {
        var work = ProductiveLearningTests.Work() with
        { Recipe = new("Crafting", Known(.1), true), LearnBeforeWork = true,
            Rates = [new(0, Known(1d)), new(1, Known(1d)), new(2, Known(1d))] };
        var batch = Start(Census(0), Read(Option("Cloth") with { SkillWork = work }), Resources(), out var comparison);
        Assert.NotNull(batch);
        var learned = Assert.Single(comparison!.Plan!.Allocations, a => a.Learning != null).Learning!;
        Assert.Equal("Crafting", learned.Skill);
        Assert.True(learned.After.StoredLevel.Value > learned.Before.StoredLevel.Value);
        Assert.Equal(learned.ObservedWorkTicks, learned.WorkTicks);
        Assert.True(learned.After.AppliedGain.Value < learned.WorkTicks * .1);
    }
}
