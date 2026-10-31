using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.Executor;
using RimGovernor.Brain.Runtime;
using Xunit;
using Amount = RimGovernor.Brain.Policy.Amount;
using GearStock = RimGovernor.Brain.GameFacts.Observations.GearStock;
using GearCandidate = RimGovernor.Brain.Policy.GearCandidate;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ClothingChoiceTests
{
    private static GearOption Garment(string product, string stuff, double cold = 5, string group = "Torso") => new() { Origin = new GearOptionOrigin.Observed(GearFixtures.Option(product + stuff, product, stuff, GearSource.Bill)), Slot = group == "Torso" ? GearSlot.SkinTorso : GearSlot.SkinLegs, Layers = ["OnSkin"], Groups = [group], Cold = cold, Cost = 20, Sharp = 0.2 };
    private static GearObservation Census(double ambient = 20, params GearOption[] options) => ColonyModelFixtures.NoGear() with
    {
        Stored = [],
        Pawns = [new() { Observed = GearFixtures.Loadout("") with { Token = "load", Pawn = new("wearer") }, Candidates = [], Apparel = [], LoadoutModel = new GearModel.Modeled(new GearLoadoutInput { Observed = GearFixtures.Loadout(""), Body = EmergencyFixtures.Placed("").Settings.Body with { ComfortableMinC = 10, ComfortableMaxC = 30 }, Threat = ColonyModelFixtures.NoThreat, Ambient = ambient, Worn = [Tattered], Options = options.Length == 0 ? [Garment("Shirt", "Cloth"), Garment("Shirt", "Wool", 15)] : EquatableArray.From(options), Role = GearFixtures.Role }) }]
    };
    // The wearer's tattered shirt and bare legs (#3816): a torso garment ends the tattered thought, pants the nakedness.
    private static GearOption Tattered => Garment("Shirt", "Cloth", 0) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Garment("Shirt", "Cloth", 0)).Origin).Value) with { Id = "tattered-shirt", Source = GearSource.Worn, Condition = 0.1 }), Sharp = 0, CareIfDamaged = true, CountsAsClothingForNudity = true };
    private static NeedThoughtStage[] ApparelStages =>
    [
        new("ApparelDamaged", ForecastApparel.Damaged, 0, -3, NeedsFixtures.Lasting((double)GameTime.TicksPerDay, false)),
        new("ApparelDamaged", ForecastApparel.Damaged, 1, -5, NeedsFixtures.Lasting((double)GameTime.TicksPerDay, false)),
        new("Naked", ForecastApparel.Naked, 0, -6, NeedsFixtures.Lasting((double)GameTime.TicksPerDay, false)),
    ];
    private static RecipeIngredientObservation Input(string id, string stuff, int count = 10, double travel = 5) =>
        new(id, stuff, count, new(id == "a-far" ? 50 : id == "c-near" ? 6 : 5, 5), count) { Capacity = 75, ApproachTicks = travel, CarryTicks = 5.0, ReturnTicks = 5.0 };
    private static RecipeActivityOption Option(string stuff, string product = "Shirt", double work = 100, string pawn = "tailor", string bench = "bench") =>
        new(product + stuff + pawn, pawn, bench, "Tailor", "Make" + product, product, stuff, "", "", new(10, 10),
            [Input(stuff, stuff)], 0.0, work, 5.0, new(15, 15))
        { RequiredCount = 10 };
    private static RecipeActivityRead Read(params RecipeActivityOption[] options) => new()
    { Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = EquatableArray.From(options), Retained = [] };
    private static ReviewFacts Facts(double cloth = 30, double wool = 30, double devilstrand = 0) => WorkResourceTestFacts.Create("tailor", "helper") with
    { Census = (WorkResourceTestFacts.Create("tailor", "helper")).Census with { Resources = [new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = "Cloth", Units = (long)cloth }, new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = "Wool", Units = (long)wool }, new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = "DevilstrandCloth", Units = (long)devilstrand }] }};
    private static WorkResourceView Resources(double cloth = 30, double wool = 30, double devilstrand = 0) => WorkRules.WorkResourcesOf(
        ProductionRules.WorkAdmissionEvidenceOf(Facts(cloth, wool, devilstrand), new ProductionBatchCensus.Read([])), new ProductionBatchCensus.Read([]), []);
    private static ReviewFacts Review(GearObservation census, RecipeActivityRead read, WorkResourceView resources)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0);
        var template = facts.ForecastObserved.Needs!.Pawns[0];
        return facts with { Gear = census, WorkView = resources, MoodBreak = new() { Threshold = 0.3, Recovery = new MoodRecovery.Measured(.25), Tiers = [new(1, 2), new(1, 3), new(1, 5)] }, ForecastObserved = facts.ForecastObserved with
        {
            Needs = facts.ForecastObserved.Needs with { Pawns = [template with { Pawn = "tailor" }, template with { Pawn = "helper" },
                template with { Pawn = "wearer", Apparel = NeedsFixtures.Apparel(RimGovernor.Brain.GameFacts.Defs.Gender.Male, true, true), ThoughtStages = [.. template.ThoughtStages, .. ApparelStages] }] },
            Coupled = CoupledFixtures.Empty with { Research = ActivityContinuationFixtures.NoResearch, Hunt = HuntFixtures.NoHunt, Tick = 100, Clothing = read, Construction = ConstructionFixtures.NoConstruction() }
        } };
    }
    private static (ForecastCoupledCandidates Candidates, ForecastCoupledSelection Selection) Compare(GearObservation census,
        RecipeActivityRead read, WorkResourceView resources)
    {
        var facts = Review(census, read, resources);
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!;
        // The colony model's horizon, as the review stamps it before the comparison (ColonyReview).
        generated = generated with { Context = generated.Context with { InvestmentHorizonDays = 10d } };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 1900d / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
        return (generated, selected);
    }
    // Material, travel, learning and yield assertions below inspect a conditional scenario.
    // The production entry leaves routine tailoring to native bills and installs no pawn activity.
    /// <summary>The bindings of the selected plan: the native bills (NativeProductionBinding) and deliveries the comparison chose.
    /// Bill work never becomes an exact order (task 5.3), so the choice is read from the bindings, not a started batch.</summary>
    private static ActivityTaskCandidate[]? WhatIf(GearObservation census, RecipeActivityRead read, WorkResourceView resources,
        out ForecastActivityOutcomeComparison? comparison)
    {
        var (generated, selected) = Compare(census, read, resources);
        comparison = selected.Selection;
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "clothing", 100));
        return selected.Selection.Plan is { Allocations.Length: > 0 } plan
            ? plan.Allocations.Select(a => generated.Context.Bindings[(a.Activity, a.Pawn)]).ToArray() : null;
    }
    private static PawnActivityRecipe Recipe(ActivityTaskCandidate[] bindings) => bindings.Single(b => b.Recipe != null).Recipe!;

    [Fact]
    public void RecipeTransitDelaysStationaryServiceEvenWithoutSkillLearning()
    {
        var row = Option("Cloth") with { TravelTicks = 20, ServicePosition = new ServicePositionRead.Located(new(10, 11)) };
        var needs = ClothingActivitiesRules.ClothingActivityRequirements(Census());
        var family = RimGovernor.Brain.Policy.Methods.Generation.ClothingMethodCandidates.Generate(Read(row), needs, 2000, Resources());
        var makes = family.Problem!.Options.Where(o => o.Equipment.Contains("bench")).ToArray();
        Assert.NotEmpty(makes);
        Assert.All(makes, make =>
        {
            Assert.Null(make.SkillWork);
            Assert.Equal(5, make.ServiceDelayTicks); // The pickup approach is already the allocation's travel leg.
            Assert.Equal(row.ServicePosition, make.ServicePosition);
        });
    }
    [Fact]
    public void OnlyCommittedConstructionScarcityChangesStuffWithoutPinningIt()
    {
        // Equal coverage and labor: the first stuff by id wins until committed construction takes it (#3330: the warmer wool
        // is no longer a thermal relief).
        var census = Census(0); var read = Read(Option("Cloth"), Option("Wool"));
        Assert.Equal("Cloth", Recipe(WhatIf(census, read, Resources(), out _)!).Stuff);
        var facts = Facts() with { CurrentConstruction = CurrentConstruction.Empty with { Deficit = new Dictionary<Resource, long> { [new("Cloth")] = 30 } } };
        var resources = WorkRules.WorkResourcesOf(ProductionRules.WorkAdmissionEvidenceOf(facts, new ProductionBatchCensus.Read([])), new ProductionBatchCensus.Read([]), []);
        Assert.Equal("Cloth", Recipe(WhatIf(census, read, resources, out _)!).Stuff); // A standing site is optional.
        var evidence = ProductionRules.WorkAdmissionEvidenceOf(Facts(), new ProductionBatchCensus.Read([])) with
        { Buildings = [new("TestWall", "Cloth", [new(new("Cloth"), 30)]) { Size = new(1, 1) }] };
        var wall = DomainAction.NewBuildingAction(new("competing-wall"), new("TestWall", new(3, 3), Rotation.North, "Cloth"), ConstructionTier.Survive);
        var committed = WorkRules.WorkResourcesOf(evidence, new ProductionBatchCensus.Read([]),
            [new(new("construction-method"), wall, new() { Action = wall.Id, Stage = Stage.Pending })]);
        Assert.Equal("Wool", Recipe(WhatIf(census, read, committed, out _)!).Stuff);
        Assert.Null(WhatIf(census, read, Resources(0, 0), out _));
    }

    [Fact]
    public void GarmentShapeRemainsOpenUntilNeedsAndWorkAreCompared()
    {
        // Both shapes relieve the same coverage. A cold ambient is no relief (#3330): with no forecast exposure to price the
        // parka's warmth, the cheaper shirt wins. ForecastCoupledSelectionTests.ColdParka covers the priced cold case.
        var options = new[] { Garment("Shirt", "Cloth", 2), Garment("Parka", "Cloth", 20) };
        var read = Read(Option("Cloth", "Shirt", 20), Option("Cloth", "Parka", 200));
        Assert.Equal("MakeShirt", Recipe(WhatIf(Census(0, options), read, Resources(), out _)!).Recipe);
        Assert.Equal("MakeParka", Recipe(WhatIf(Census(20, options), Read(Option("Cloth", "Shirt", 200), Option("Cloth", "Parka", 20)), Resources(), out _)!).Recipe);
    }

    [Fact]
    public void IngredientStackAlternativesIncludeSplitInputsAndTravelChangesChoice()
    {
        var row = Option("Cloth") with { Inputs = [Input("a-far", "Cloth", 10, 300), Input("b-near", "Cloth", 6), Input("c-near", "Cloth", 4)] };
        var selected = Recipe(WhatIf(Census(), Read(row), Resources(), out _)!);
        Assert.Equal(["b-near", "c-near"], selected.Ingredients.Select(i => i.Thing).Order().ToArray());
        Assert.Equal(10, selected.Ingredients.Sum(i => i.Count));
        row = row with { Inputs = [Input("a-far", "Cloth", 10, 1), Input("b-near", "Cloth", 6, 300), Input("c-near", "Cloth", 4, 300)] };
        Assert.Equal("a-far", Assert.Single(Recipe(WhatIf(Census(), Read(row), Resources(), out _)!).Ingredients).Thing);
    }

    [Fact]
    public void RetainingObservedCurrentProductAndPostponingAreRealComparisonResults()
    {
        var read = Read(Option("Cloth")) with { Retained = [new("running-uft", "Shirt", "Cloth", 25.0)] };
        Assert.Null(WhatIf(Census(), read, Resources(), out var retained));
        Assert.StartsWith("retain-", Assert.Single(retained!.Selected));
        Assert.Null(WhatIf(Census(), Read(Option("Cloth", work: 3000)), Resources(), out var postpone));
        Assert.Empty(postpone!.Selected);
    }

    [Fact]
    public void UnfinishedWorkUsesRemainingWorkAndDoesNotSpendLooseIngredientsAgain()
    {
        var unfinished = Option("Cloth") with { Bill = "owned-bill", Unfinished = "unfinished-shirt", Inputs = [], WorkTicks = 10.0 };
        var result = WhatIf(Census(), Read(unfinished), Resources(0, 0), out _)!;
        Assert.Equal("unfinished-shirt", Recipe(result).Unfinished);
        Assert.Empty(Recipe(result).Ingredients);
        Assert.Equal("Cloth", result.Single(b => b.Kind == PawnActivityKind.Deliver).ProductStuff);
    }

    [Fact]
    public void ExistingExactBillCanExecuteItsAlreadyHeldInputsButCannotFundAnotherRecipe()
    {
        // Stock is physical total; a reservation removes availability, not the actual items.
        // The former local selector incorrectly accepted a promise even with zero physical stock.
        var empty = Resources(0, 0).Evidence with
        { Promises = AdmissionAssertions.MaterialPromises([("existing", [new(new("Cloth"), new MaterialUnits.Exact(10.0), "standing-bill")])]) };
        Assert.Null(WhatIf(Census(), Read(Option("Cloth") with { Bill = "standing-bill" }),
            WorkRules.WorkResourcesOf(empty, new ProductionBatchCensus.Read([]), []), out _));
        var evidence = Resources(10, 0).Evidence with { Promises = empty.Promises };
        var resources = WorkRules.WorkResourcesOf(evidence, new ProductionBatchCensus.Read([]), []);
        var selected = WhatIf(Census(), Read(Option("Cloth") with { Bill = "standing-bill" }), resources, out _)!;
        Assert.Equal("standing-bill", Recipe(selected).Bill);
        Assert.Null(WhatIf(Census(), Read(Option("Cloth")), resources, out _));
        evidence = Resources(100, 0).Evidence with
        { Promises = AdmissionAssertions.MaterialPromises([("existing", [new(new("Cloth"), new MaterialUnits.Exact(100.0), "standing-bill")])]) };
        resources = WorkRules.WorkResourcesOf(evidence, new ProductionBatchCensus.Read([]), []);
        var other = Option("Cloth", "Pants", pawn: "helper", bench: "other-bench") with
        { Inputs = [Input("other-stack", "Cloth")], Destination = new(16, 15) };
        var bundle = WhatIf(Census(20, Garment("Shirt", "Cloth"), Garment("Pants", "Cloth", group: "Legs")),
            Read(Option("Cloth") with { Bill = "standing-bill" }, other), resources, out _)!;
        Assert.Single(bundle, b => b.Recipe != null);
    }

    [Fact]
    public void ComplementaryGarmentsCanShareAStackButCannotDoubleSpendItOrTheirDestination()
    {
        var census = Census(20, Garment("Shirt", "Cloth"), Garment("Pants", "Cloth", group: "Legs"));
        var shirt = Option("Cloth") with { Inputs = [Input("shared", "Cloth", 20)] };
        var pants = Option("Cloth", "Pants", pawn: "helper", bench: "other-bench") with
        { Inputs = [Input("shared", "Cloth", 20)], Destination = new(16, 15) };
        var together = WhatIf(census, Read(shirt, pants), Resources(), out _)!;
        Assert.Equal(2, together.Count(b => b.Recipe != null));
        Assert.Equal(20, together.Where(b => b.Recipe != null).Sum(b => b.Recipe!.Ingredients.Sum(i => i.Count)));
        var tooSmall = Read(shirt with { Inputs = [Input("shared", "Cloth", 15)] }, pants with { Inputs = [Input("shared", "Cloth", 15)] });
        Assert.Single(WhatIf(census, tooSmall, Resources(), out _)!, b => b.Recipe != null);
        Assert.Single(WhatIf(census, Read(shirt, pants with { Destination = shirt.Destination }), Resources(), out _)!, b => b.Recipe != null);
    }

    [Fact]
    public void RetainedGarmentCannotAddProtectionAlongsideAConflictingReplacement()
    {
        var census = Census(0, Garment("Shirt", "Cloth", 5), Garment("Parka", "Cloth", 10));
        var read = Read(Option("Cloth", "Parka")) with { Retained = [new("ongoing", "Shirt", "Cloth", 10.0)] };
        WhatIf(census, read, Resources(), out var comparison);
        Assert.Single(comparison!.Selected);
    }

    [Fact]
    public void PhysicalReplacementsCompeteWithCrafting()
    {
        var census = Census();
        // #3621: a physical replacement no longer withholds the craft; the comparison weighs the swap against it.
        var physical = census with { Pawns = [census.Pawns[0] with { Candidates = [new(new RimGovernor.Brain.GameFacts.Observations.GearCandidate { Item = GearFixtures.Item("stored-shirt", new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Normal)), Gain = 1 }, new("Shirt"))] }] };
        Assert.Equal(ClothingActivitiesRules.ClothingActivityRequirements(census), ClothingActivitiesRules.ClothingActivityRequirements(physical));
        Assert.NotEmpty(ClothingActivitiesRules.ClothingActivityRequirements(physical));
    }

    private static GearOption Duster(string stuff, double sharp, GearSource source = GearSource.Bill) => Garment("Duster", stuff) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Garment("Duster", stuff)).Origin).Value) with { Id = source == GearSource.Bill ? "Duster" + stuff : source + "-duster-" + stuff, Source = source }), Slot = GearSlot.Outer, Layers = ["Shell"], Sharp = sharp, Cold = 0 };

    private static GearObservation Wearing(params GearOption[] options) => Census(20, options) with
    {
        Pawns = [Census(20, options).Pawns[0] with { LoadoutModel = new GearModel.Modeled(Census(20, options).Pawns[0].Model! with
        { Worn = [Garment("Shirt", "Cloth", 0) with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Garment("Shirt", "Cloth", 0)).Origin).Value) with { Id = "worn-shirt", Source = GearSource.Worn }), Sharp = 0 }, Duster("Cloth", 0.1, GearSource.Worn)] }) }]
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
        var worn = Wearing().Pawns[0].Model!.Worn;
        var dusterSwap = new ForecastCommitment("gear/wearer/a", []) { ExclusiveOutcome = "gear/wearer",
            OutfitChanges = [new("wearer", [worn[0], Duster("DevilstrandCloth", 0.5, GearSource.Stored)])] };
        var shirtSwap = new ForecastCommitment("gear/wearer/b", []) { ExclusiveOutcome = "gear/wearer",
            OutfitChanges = [new("wearer", [Garment("Shirt", "Wool") with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Garment("Shirt", "Wool")).Origin).Value) with { Id = "stored-shirt", Source = GearSource.Stored }) }, worn[1]])] };
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
        { MedicalPawns = [CareFixtures.Patient("wearer")] };
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
    public async Task NormalDemandComparisonJournalAndHandsCreateAnUnpinnedNativeBillWithoutCreditingOutput()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
        var path = Path.Combine(Path.GetTempPath(), "rg-clothing-" + Guid.NewGuid().ToString("N") + ".db");
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(path, ct);
        journal.Review(new ReviewRequest
        {
            Current = world,
            Tick = new(100),
            Enabled = true,
            Policy = Rules.DefaultPlannerSettings(),
            Facts = Facts() with { Gear = Census(), WorkAdmission = new()
                { Recipes = [new("MakeShirt", [[new(new("Cloth"), 10)]], true) { Products = [], WorkAmount = 0 }] } }
        }, ct);
        var (generated, selected) = Compare(Census(), Read(Option("Cloth")), journal.WorkResources(world, ct));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "clothing", 100));
        var bill = Assert.Single(ForecastCoupledSelection.SelectedProductionBills(selected, generated));
        Assert.Equal("", bill.Worker);
        Assert.Equal(["Cloth"], bill.Ingredients);
        var plan = new PlanSpec(new("make-clothing"), new(1), [DomainAction.NewProductionBillAction(new("bill-action"), bill)]);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(AdminFamily.WorkLedger, world, new("clothing"), "", plan, ct).Outcome);
        Assert.Equal(ClaimOutcome.Continued, journal.CommitClaimedMethod(AdminFamily.WorkLedger, world, new("clothing"), "", plan, ct).Outcome);
        var stored = journal.LoadPlan(plan.Id, ct).Spec.Actions[0];
        var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.ProductionBillCase>(IntentActionRules.IntentAction(stored.Id.Value, stored).Intent).Value;
        Assert.Equal("MakeShirt", intent.RecipeDef);
        Assert.Null(intent.Settings!.Worker);
        Assert.Equal(1, Assert.IsType<RimGovernor.Brain.GameOrders.Operations.BillRepetition.Count>(intent.Settings.Repetition).Repetitions);
        var native = new NativeBill();
        using var hands = new RimGovernor.Brain.Executor.Executor(new ExecutorJournal(journal), native, new Clock(), new Limits
        {
            MaxAge = TimeSpan.FromSeconds(30), RunTimeout = TimeSpan.FromSeconds(10), JournalTimeout = TimeSpan.FromSeconds(10)
        }, new PlanAuthorizer(journal, true));
        hands.UpdateAuthority(new() { Snapshot = world, Enabled = true });
        Assert.Null(Assert.Single(await hands.RunBatchAsync(plan.Id, [stored.Id], ct)).Error);
        Assert.Equal(intent, native.Written);
        Assert.Null(Assert.Single(await hands.RunBatchAsync(plan.Id, [stored.Id], ct)).Error);
        Assert.Equal(1, native.Writes);
        Assert.Empty(journal.WorkResources(world, ct).Claims.Pawns);
        Assert.DoesNotContain(journal.WorkActions(world, ct).Actions, a => a.Action.PawnActivities is not null);
        Assert.Equal(Stage.Completed, Assert.Single(journal.LoadPlan(plan.Id, ct).Progress).View.Stage);
        // The receipt acknowledges bill creation, not a completed garment or delivered output.
        Assert.Equal(30, journal.WorkResources(world, ct).Evidence.Stock.Single(s => s.Resource == new Resource("Cloth")).Count);
    }

    [Theory]
    [InlineData("existing-bill", "")]
    [InlineData("existing-bill", "unfinished")]
    public void SelectedExistingClothingIntentCreatesNoDuplicateBill(string bill, string unfinished)
    {
        var row = Option("Cloth") with { Bill = bill, Unfinished = unfinished };
        var (generated, selected) = Compare(Census(), Read(row), Resources());
        Assert.NotEmpty(selected.Selection.Selected);
        Assert.Empty(ForecastCoupledSelection.SelectedProductionBills(selected, generated));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selected, generated, 1, "clothing", 100));
    }

    [Fact]
    public void ClothingDemandEnablesNativeWorkBeforeABillExists()
    {
        Assert.Equal(1, WorkDemandRules.DeficitTargets(new Dictionary<Resource, long>(), false, Census())[new("Shirt")]);
    }

    private sealed class Clock : IClock { public DateTimeOffset Now() => DateTimeOffset.UtcNow; }
    private sealed class NativeBill : IBoundary
    {
        public int Writes;
        public RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent? Written;
        public Task<IntentInspection> InspectIntentAsync(Target target, CancellationToken ct) => Task.FromResult(new IntentInspection
        { StartedAt = DateTimeOffset.UtcNow, ObservedAt = DateTimeOffset.UtcNow, Current = target.Snapshot, Tick = new(100) });
        public Task<IReadOnlyList<RimGovernor.Brain.Executor.Receipt>> WriteIntentsAsync(IReadOnlyList<Placement> placements, CancellationToken ct)
        {
            Writes++;
            var placement = Assert.Single(placements);
            Written = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.ProductionBillCase>(
                IntentActionRules.IntentAction(placement.Action.Id.Value, placement.Action).Intent).Value;
            return Task.FromResult<IReadOnlyList<RimGovernor.Brain.Executor.Receipt>>([new()
                { Action = placement.Action.Id, Attempt = placement.Attempt, Snapshot = placement.Snapshot, Kind = Receipt.Accepted }]);
        }
    }
    [Fact]
    public void SelectedGeneratedTailoringLearnsDuringWorkButNotIngredientCarrying()
    {
        var work = ProductiveLearningTests.Work() with
        { Recipe = new("Crafting", .1, true), LearnBeforeWork = true,
            Rates = [new(0, 1d), new(1, 1d), new(2, 1d)] };
        var batch = WhatIf(Census(0), Read(Option("Cloth") with { SkillWork = work }), Resources(), out var comparison);
        Assert.NotNull(batch);
        var learned = Assert.Single(comparison!.Plan!.Allocations, a => a.Learning != null).Learning!;
        Assert.Equal("Crafting", learned.Skill);
        Assert.True(learned.After.StoredLevel > learned.Before.StoredLevel);
        Assert.Equal(learned.ObservedWorkTicks, learned.WorkTicks);
        Assert.True(learned.After.AppliedGain < learned.WorkTicks * .1);
    }
}
