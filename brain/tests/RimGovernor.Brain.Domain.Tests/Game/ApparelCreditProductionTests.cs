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

    private static O.GearLoadoutOption Option(string id, string source, double condition) =>
        new() { Id = id, DefName = ShirtDef, Stuff = "Cloth", Quality = 2, Source = source, Condition = condition };

    // The census of one colonist in a tattered shirt, decoded by the observation read.
    private static GearObservation Census()
    {
        var row = new O.GearLoadout
        {
            Pawn = new C.Ref { Id = "pawn" },
            Equipment = new O.PawnEquipment(),
            ApparelPolicy = new O.ApparelPolicyState(),
            Gender = D.Gender.Male,
            ComfortableMinC = 10,
            ComfortableMaxC = 30,
            LoadoutModel = new O.GearLoadoutModel { Worn = [Option("worn-shirt", "worn", 0.1)], Options = [Option("bill-shirt", "bill", 1)] },
        };
        var defs = new GearDefinitions { Catalog = Catalog(), Finished = Known<IReadOnlySet<string>>(new HashSet<string>()) };
        var model = ColonyGearReads.GearLoadoutModelFacts(defs, new Tables(default, default, Things.Of([])), 20, row,
            Known(new RimGovernor.Brain.Policy.ApparelPolicyState()), Unknown<RaidThreat>(), new StuffArrival(new Dictionary<Resource, long> { [new("Cloth")] = 100 }, []));
        return new GearObservation
        {
            Stored = Known(default(EquatableArray<GearStock>)),
            Pawns = [new GearPawn { Pawn = new("pawn"), Loadout = "load", Candidates = Known(default(EquatableArray<GearCandidate>)), Apparel = Known(default(EquatableArray<GearApparel>)), LoadoutModel = model }],
        };
    }

    private static O.NeedThoughtStage Stage(int stage, double offset) => new("ApparelDamaged", ForecastApparel.Damaged, stage, offset, Known((double)GameTime.TicksPerDay), false);

    // The pawn's needs row as the native read writes it, with or without the apparel facts, and the shirt bill the clothing
    // read observes running.
    private static ForecastCoupledContext Generate(O.NeedApparel? apparel)
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
            Gear = Known(Census()),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs with { Pawns = [template with { Apparel = apparel, ThoughtStages = [Stage(0, -3), Stage(1, -5)] }] },
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

    [Fact]
    public void AFreshColonyWithoutApparelFactsGeneratesWithoutAThrowAndNoShift()
    {
        var context = Generate(null);
        Assert.False(context.ApparelShifts.ContainsKey("pawn"));
    }
}
