using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using C = RimGovernor.Contracts.Wire.Common;
using D = RimGovernor.Contracts.Wire.Defs;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3816: the facts the garment credit reads come from the production reads, not injected options: a garment's
// careIfDamaged and countsAsClothingForNudity from the catalog's apparel row through the gear census decode, its retained
// bill's outfit change from the clothing read, and the break shift from the pawn's apparel thought rows. A fresh colony
// whose needs read carries no apparel facts generates its alternatives without a throw.
public sealed class ApparelCreditProductionTests
{
    private const string ShirtDef = "Apparel_BasicShirt";
    private static readonly ThingDef Cloth = new() { defName = "Cloth" };

    private static ThingDef Shirt() => new()
    {
        defName = ShirtDef,
        apparel = new ApparelProperties
        {
            bodyPartGroups = [new BodyPartGroupDef { defName = "Torso" }],
            layers = [new ApparelLayerDef { defName = "OnSkin" }],
        },
    };

    private static DefinitionCatalog Catalog()
    {
        var shirt = Shirt();
        var shown = new DefStatTable.Value(0.1f, true, null);
        var values = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>
        {
            [(shirt, Cloth, CatalogStats.ArmorSharp)] = shown, [(shirt, Cloth, CatalogStats.ArmorBlunt)] = shown,
            [(shirt, Cloth, CatalogStats.InsulationCold)] = shown, [(shirt, Cloth, CatalogStats.InsulationHeat)] = shown,
        };
        var table = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(shirt, Cloth)] = new([new StatCost("Cloth", 40)], null) },
            values, new Dictionary<BuildableDef, IReadOnlyList<string>>(), new HashSet<CatalogStat>(CatalogStats.All));
        var recipe = new RecipeDef { defName = "Make_" + ShirtDef, products = [new ThingDefCountClass(shirt, 1)] };
        return new([Cloth, shirt, new TerrainDef { defName = "Soil" }, recipe], table, new CatalogDerived("Cloth", "Wort", 10f));
    }

    private static O.GearLoadoutOption Option(string id, GearSource source, double condition, D.QualityCategory? quality = D.QualityCategory.Normal) =>
        GearFixtures.Option(id, ShirtDef, "Cloth", source) with
        {
            Condition = condition,
            Quality = quality is { } graded ? new O.ItemQuality.Graded(graded) : new O.ItemQuality.Qualityless(),
        };

    // A fresh colony's crafter as the native bill row carries it (#3850): a middling Crafting skill, uninspired, no role.
    private static readonly O.GearCrafter Crafter = new(6, false, 0);

    // The census of one colonist in a tattered shirt (or, with a worn row, that shirt), decoded by the observation read; the shirt
    // bill names its crafters.
    private static GearObservation Census(bool dressed = true, O.GearLoadoutOption? worn = null, EquatableArray<O.GearCrafter>? crafters = null,
        O.GearLoadoutOption? bill = null)
    {
        var row = GearFixtures.Loadout("pawn") with
        {
            ApparelPolicy = PoliciesTests.EmptyOutfit,
            LoadoutModel = GearFixtures.NoModel with { Worn = dressed ? [worn ?? Option("worn-shirt", GearSource.Worn, 0.1)] : [], Options = [bill ?? Option("bill-shirt", GearSource.Bill, 1) with { Crafters = crafters ?? [Crafter] }] },
        };
        var defs = new GearDefinitions { Catalog = Catalog(), Finished = Known<IReadOnlySet<string>>(new HashSet<string>()) };
        var model = ColonyGearReads.GearLoadoutModelFacts(defs, new Tables(default, default, Things.Of([])), 20, row,
            new RimGovernor.Brain.Policy.ApparelPolicyState(), Unknown<RaidThreat>(), new StuffArrival(new Dictionary<Resource, long> { [new("Cloth")] = 100 }, []));
        return ColonyModelFixtures.NoGear() with
        {
            Stored = [],
            Pawns = [new GearPawn { Pawn = new("pawn"), Loadout = "load", Candidates = [], Apparel = [], LoadoutModel = model }],
        };
    }

    private static O.NeedThoughtStage Stage(int stage, double offset) => new("ApparelDamaged", ForecastApparel.Damaged, stage, offset, Known((double)GameTime.TicksPerDay), false);

    // The pawn's needs row as the native read writes it, with or without the apparel facts, and the shirt bill the clothing
    // read observes running.
    private static ForecastCoupledContext Generate(O.NeedApparel? apparel, bool dressed = true, O.GearLoadoutOption? worn = null,
        EquatableArray<O.GearCrafter>? crafters = null)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0);
        var template = facts.ForecastObserved.Needs!.Pawns[0];
        var clothing = new O.RecipeActivityRead
        {
            Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 },
            Retained = [new("running", ShirtDef, "Cloth", Known(25.0))],
        };
        facts = facts with
        {
            Gear = Known(Census(dressed, worn, crafters)),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs with { Pawns = [template with { Apparel = apparel, ThoughtStages = [Stage(0, -3), Stage(1, -5), TitleStage, QualityStage] }] },
                Coupled = facts.ForecastObserved.Coupled! with { Clothing = clothing },
            },
        };
        return ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context;
    }

    [Fact]
    public void TheCensusDecodeCarriesTheCatalogsDamageAndNudityFacts()
    {
        var input = Census().Pawns[0].LoadoutModel.Value;
        var worn = Assert.Single(input.Worn);
        Assert.True(worn.CareIfDamaged);
        Assert.True(worn.CountsAsClothingForNudity);
        Assert.Equal(0.1, worn.Condition);
        Assert.True(Assert.Single(input.Options).CountsAsClothingForNudity);
    }

    [Fact]
    public void ARetainedShirtBillOverATatteredShirtShiftsTheWearersBreaksByTheTatteredStage()
    {
        Assert.Single(ClothingActivitiesRules.ClothingActivityRequirements(Census()));
        var context = Generate(new(D.Gender.Male, true, true));
        Assert.Equal([0.05], context.ApparelShifts["pawn"]);
    }

    // #3836: the title's unmet-requirement thought, read for every pawn under Royalty (inactive without a requirement).
    private static readonly O.NeedThoughtStage TitleStage = new("TitleApparelRequirementNotMet", ForecastApparel.TitleApparel, 0, -4, default, false);

    [Fact]
    public void AnUndressedTitledColonistsRequiredShirtBillShiftsTheirBreaksByTheRequirementThought()
    {
        var titled = new O.NeedApparel(D.Gender.Male, true, true) { Requirements = [new(ForecastApparel.TitleApparel, [ShirtDef])] };
        Assert.Equal([0.04], Generate(titled, dressed: false).ApparelShifts["pawn"]);
    }

    [Fact]
    public void AFreshColonysUntitledColonistGetsNothingFromTheRequirementThought()
    {
        // A fresh colony's colonist has no title and no role: the native read writes no requirement, and the shirt changes
        // only what the other workers read (a male pawn's torso is no nakedness), so it shifts nothing.
        var context = Generate(new(D.Gender.Male, true, true), dressed: false);
        Assert.False(context.ApparelShifts.ContainsKey("pawn"));
    }

    [Fact]
    public void AFreshColonyWithoutApparelFactsGeneratesWithoutAThrowAndNoShift()
    {
        var context = Generate(null);
        Assert.False(context.ApparelShifts.ContainsKey("pawn"));
    }

    // #3841: the title's minimum-quality thought, read for every pawn under Royalty (inactive with no title in effect).
    private static readonly O.NeedThoughtStage QualityStage = new("TitleApparelMinQualityNotMet", ForecastApparel.TitleApparelQuality, 0, -4, default, false);

    private static readonly O.NeedApparel Knight = new(D.Gender.Male, true, true) { MinimumQuality = D.QualityCategory.Normal };

    [Fact]
    public void ATitledColonistsNormalShirtBillOverAPoorShirtShiftsTheirBreaksByTheMinimumQualityThought()
    {
        var poor = Option("worn-shirt", GearSource.Worn, 1, quality: D.QualityCategory.Poor);
        // The same def and groups at a higher quality is a candidate: the worker reads the lowest worn quality.
        Assert.Single(ClothingActivitiesRules.ClothingActivityRequirements(Census(worn: poor)));
        Assert.Equal([0.04], Generate(Knight, worn: poor).ApparelShifts["pawn"]);
    }

    [Fact]
    public void AFreshColonysUntitledColonistInAPoorShirtGetsNothingFromTheMinimumQualityThought()
    {
        // No title in effect: the native read writes the Awful minimum, which no garment is below.
        var context = Generate(new(D.Gender.Male, true, true), worn: Option("worn-shirt", GearSource.Worn, 1, quality: D.QualityCategory.Poor));
        Assert.False(context.ApparelShifts.ContainsKey("pawn"));
    }

    [Fact]
    public void AQualitylessWornGarmentDecodesAsQualitylessAndCountsTowardNoMinimum()
    {
        var plain = Option("worn-shirt", GearSource.Worn, 1, quality: null);
        var worn = Assert.Single(Census(worn: plain).Pawns[0].LoadoutModel.Value.Worn);
        Assert.True(worn.Qualityless);
        // The quality-less shirt counts neither way; the only shift is the crafted bill replacement's chance of falling
        // below Knight's Normal minimum.
        Assert.Equal([-0.04], Generate(Knight, worn: plain).ApparelShifts["pawn"]);
    }

    // #3850: the shirt bill's quality is its best crafter's odds, decoded from the crafters the native bill row carries.
    [Fact]
    public void AShirtBillDecodesAtItsBestCraftersQualityOdds()
    {
        O.GearCrafter novice = new(3, false, 0), master = new(15, false, 0), inspired = new(8, true, 0);
        GearOption Bill(EquatableArray<O.GearCrafter> crafters) => Assert.Single(Census(crafters: crafters).Pawns[0].LoadoutModel.Value.Options);
        Assert.Equal(CraftedQualityRules.Odds(Crafter), Bill([Crafter]).QualityOdds);
        Assert.Equal(CraftedQualityRules.Odds(master), Bill([novice, master]).QualityOdds);
        Assert.Equal(CraftedQualityRules.Odds(inspired), Bill([master, inspired]).QualityOdds);
        // A bill with a quality and no crafter is an unread fact (the native read offers no bill nobody can craft).
        Assert.Throws<RequiredFactException>(() => Census(crafters: []));
        // A bill without quality is at none.
        var plain = Assert.Single(Census(bill: Option("bill-shirt", GearSource.Bill, 1, quality: null)).Pawns[0].LoadoutModel.Value.Options);
        Assert.True(plain.Qualityless);
        Assert.Empty(plain.QualityOdds);
    }

    [Fact]
    public void AFreshColonysShirtBillLiftsAnExcellentMinimumOnlyAsAChanceAndAnUntitledColonistGetsNothing()
    {
        var count = new O.NeedApparel(D.Gender.Male, true, true) { MinimumQuality = D.QualityCategory.Excellent };
        var good = Option("worn-shirt", GearSource.Worn, 1, quality: D.QualityCategory.Good);
        // The fresh colony's crafter may make an Excellent shirt: a candidate whose lifting outcome is the thought's shift.
        Assert.Single(ClothingActivitiesRules.ClothingActivityRequirements(Census(worn: good)));
        Assert.Equal([0.04], Generate(count, worn: good).ApparelShifts["pawn"]);
        Assert.False(Generate(new(D.Gender.Male, true, true), worn: good).ApparelShifts.ContainsKey("pawn"));
    }
}
