using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3623: the clothing runway replaces a wearing garment in the stuff tier 2 chose for the pawn's slot (ReviewFacts.ClothingStuff),
// the worn stuff where tier 2 chose none, so a hunt serves the demand only for the chosen leather.
public sealed class ClothingChosenStuffTests
{
    private const string Parka = "Apparel_Parka";

    private static readonly Resource Plain = new("Leather_Plain");

    private static readonly Resource Bear = new("Leather_Bear");

    private static readonly Resource Cloth = new("Cloth");

    // p1 wears a tattered plain-leather parka; the parka takes 80 of any leather.
    private static ReviewFacts Facts(params (GearSlot Slot, Resource Stuff)[] chosen) => new()
    {
        Gear = ColonyModelFixtures.NoGear() with
        {
            Pawns =
            [
                new GearPawn { Observed = GearFixtures.Loadout("") with { Token = "", Pawn = new("p1") }, LoadoutModel = new GearModel.Modeled(new GearLoadoutInput { Observed = GearFixtures.Loadout(""), Body = EmergencyFixtures.Placed("").Settings.Body with { ComfortableMinC = 0, ComfortableMaxC = 0 }, Threat = ColonyModelFixtures.NoThreat, Worn = [new GearOption { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("worn1", Parka, (Plain).Value, GearSource.Worn) with { Condition = 0.4, Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Awful) }), Slot = GearSlot.Outer }], Role = GearFixtures.Role }) },
            ],
        },
        Census = ColonyFactsCoreTests.Snapshot() with { Resources = [] },
        Garments = [new ClothingGarment { Definition = new(Parka), Groups = ["Torso"], Slots = [[new Amount(Plain, 80), new Amount(Bear, 80)]] }],
        ClothingStuff = chosen.ToDictionary(c => (new PawnId("p1"), c.Slot), c => c.Stuff),
    };

    private static AcquisitionSource Hunt(string id, Resource leather) =>
        new() { Observed = new() { Source = new() { Id = id }, Token = id, Resource = "Meat_Deer", Yield = 1, Designation = new RimGovernor.Brain.GameFacts.Observations.AcquisitionDesignation.Open(), Taken = false, Kind = new RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Hunt(82, 1, false, false, false, false) },
        Entity = new() { Id = id, DefName = id, Label = id, Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, new Cell(0, 0)) }, NutritionYield = 4, Products = [new SourceProduct(leather, 40)] };

    [Fact]
    public void TheRunwayDemandsTheChosenStuffNotTheWornOne()
    {
        Assert.Equal(new Dictionary<Resource, long> { [Bear] = 80 }, Facts((GearSlot.Outer, Bear)).ClothingRunway().Needs);
    }

    [Fact]
    public void WithoutAWinnerForTheSlotTheRunwayFallsBackToTheWornStuff()
    {
        Assert.Equal(new Dictionary<Resource, long> { [Plain] = 80 }, Facts().ClothingRunway().Needs);
        // A winner for another slot is not this garment's.
        Assert.Equal(new Dictionary<Resource, long> { [Plain] = 80 }, Facts((GearSlot.SkinTorso, Bear)).ClothingRunway().Needs);
    }

    [Fact]
    public void AChosenStuffTheWornGarmentsRecipeCannotTakeFallsBackToTheWornStuff()
    {
        Assert.Equal(new Dictionary<Resource, long> { [Plain] = 80 }, Facts((GearSlot.Outer, Cloth)).ClothingRunway().Needs);
    }

    [Fact]
    public void AHuntForTheChosenLeatherCountsTowardTheDemandAndOneForAnotherLeatherDoesNot()
    {
        // ResourceDemandOf's clothing set: the stuffs the runway demands.
        var clothing = Facts((GearSlot.Outer, Bear)).ClothingRunway().Needs.Keys.ToHashSet();
        var source = Hunt("bear", Bear);
        var observed = source.Observed;
        var bear = ClothingDemandRules.ResourceYieldFor(source, Bear, clothing);
        Assert.NotNull(bear);
        Assert.Equal(40, bear.Value);
        Assert.Same(observed, source.Observed);
        Assert.Equal(1, source.Yield);
        Assert.NotEqual(Bear.Value, source.Resource);
        Assert.Null(ClothingDemandRules.ResourceYieldFor(Hunt("deer", Plain), Plain, clothing));
        Assert.Null(ClothingDemandRules.ResourceYieldFor(Hunt("deer", Plain), Bear, clothing));
    }
}
