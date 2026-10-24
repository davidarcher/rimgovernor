using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3623: the clothing runway replaces a wearing garment in the stuff tier 2 chose for the pawn's slot (RoundsFacts.ClothingStuff),
// the worn stuff where tier 2 chose none, so a hunt serves the demand only for the chosen leather.
public sealed class ClothingChosenStuffTests
{
    private const string Parka = "Apparel_Parka";

    private static readonly Resource Plain = new("Leather_Plain");

    private static readonly Resource Bear = new("Leather_Bear");

    private static readonly Resource Cloth = new("Cloth");

    // p1 wears a tattered plain-leather parka; the parka takes 80 of any leather.
    private static RoundsFacts Facts(params (GearSlot Slot, Resource Stuff)[] chosen) => new()
    {
        Gear = Known(ColonyModelFixtures.NoGear() with
        {
            Pawns =
            [
                new GearPawn
                {
                    Pawn = new("p1"),
                    LoadoutModel = Known(new GearLoadoutInput
                    {
                        Worn = [new GearOption { ID = "worn1", Definition = new(Parka), Stuff = Plain, Slot = GearSlot.Outer, Source = GearSource.Worn, Condition = 0.4 }],
                    }),
                },
            ],
        }),
        Resources = Known<EquatableArray<Amount>>([]),
        Garments = [new ClothingGarment { Definition = new(Parka), Groups = ["Torso"], Slots = [[new Amount(Plain, 80), new Amount(Bear, 80)]] }],
        ClothingStuff = chosen.ToDictionary(c => (new PawnId("p1"), c.Slot), c => c.Stuff),
    };

    private static AcquisitionSource Hunt(string id, Resource leather) =>
        new() { Id = id, Definition = id, Hunt = true, Food = true, NutritionYield = 4, Products = [new SourceProduct(leather, 40)] };

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
        var bear = ClothingDemandRules.ResourceSourceFor(Hunt("bear", Bear), Bear, clothing);
        Assert.NotNull(bear);
        Assert.Equal(40, bear.Yield);
        Assert.Null(ClothingDemandRules.ResourceSourceFor(Hunt("deer", Plain), Plain, clothing));
        Assert.Null(ClothingDemandRules.ResourceSourceFor(Hunt("deer", Plain), Bear, clothing));
    }
}
