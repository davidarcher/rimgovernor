using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The gear records read whole (#3904): the insulation floor, the arrival, the gear patch and the empty census are named states.</summary>
public sealed class GearNamedStatesTests
{
    private static GearOption Shirt(GearArrival arrival) => new()
    {
        ID = "bill:Shirt/Cloth",
        Definition = new("Shirt"),
        Stuff = new("Cloth"),
        Slot = GearSlot.SkinTorso,
        Layers = ["OnSkin"],
        Groups = ["Torso"],
        Source = GearSource.Bill,
        Quality = 2,
        Condition = 1,
        Sharp = .2,
        Cost = 20,
        Arrival = arrival,
    };

    private static GearLoadoutInput Input(GearOption option) =>
        new() { Threat = ColonyModelFixtures.NoThreat, Ambient = 20, ComfortableMin = 10, ComfortableMax = 30, Options = [option] };

    [Fact]
    public void EveryPlannedWorkTypeDressesForARole()
    {
        // #3923: the mod's Training work type, which every colonist has, dresses for outdoor labour; a missing arm threw at tick 0.
        foreach (var work in Enum.GetValues<WorkType>().Where(w => w != WorkType.Unrecognized))
        {
            var role = GearLoadoutRules.DeriveGearRole(new GearRoleInput { Work = [new WorkPriority { Work = work, Priority = 1 }] });
            Assert.True(Enum.IsDefined(role), work.ToString());
        }

        Assert.Equal(GearRole.Worker, GearLoadoutRules.DeriveGearRole(new GearRoleInput { Work = [new WorkPriority { Work = WorkType.Training, Priority = 1 }] }));
    }

    [Fact]
    public void AWornFloorReadsTheWornInsulationAndAChosenFloorItsOwnValue()
    {
        Assert.Equal(-4.0, new InsulationFloor.Worn().Or(-4));
        Assert.Equal(12.0, new InsulationFloor.Chosen(12).Or(-4));
        Assert.IsType<InsulationFloor.Worn>(new GearPawn { Pawn = new("pawn"), LoadoutModel = new GearModel.NoOutfit(), Candidates = [] }.ColdFloor);
    }

    [Fact]
    public void TheCensusStampCarriesAChosenFloorAndLeavesTheRestWorn()
    {
        var gear = ColonyModelFixtures.NoGear() with
        {
            Pawns =
            [
                new GearPawn { Pawn = new("chosen"), LoadoutModel = new GearModel.NoOutfit(), Candidates = [] },
                new GearPawn { Pawn = new("unchosen"), LoadoutModel = new GearModel.NoOutfit(), Candidates = [], ColdFloor = new InsulationFloor.Chosen(3) },
            ],
        };
        var stamped = RimGovernor.Brain.BuildingRuntime.Rounder.WithGearChoices(new RoundsFacts { Gear = Known(gear) }, [new GearChoice("chosen", false, new InsulationFloor.Chosen(-10), new InsulationFloor.Worn())]).Gear.Value.Pawns;
        Assert.Equal(new InsulationFloor.Chosen(-10), stamped[0].ColdFloor);
        Assert.IsType<InsulationFloor.Worn>(stamped[0].HeatFloor);
        // A pawn the comparator names no choice for is back at its worn insulation.
        Assert.IsType<InsulationFloor.Worn>(stamped[1].ColdFloor);
    }

    [Fact]
    public void AnOptionWithNoKnownArrivalOrOneBeyondTierOneIsNotEligible()
    {
        Assert.True(GearLoadoutRules.GearEligible(Input(Shirt(new GearArrival.InDays(0))), Shirt(new GearArrival.InDays(0))));
        Assert.True(GearLoadoutRules.GearEligible(Input(Shirt(new GearArrival.InDays(ForecastCoupled.TierOneHorizonDays))), Shirt(new GearArrival.InDays(ForecastCoupled.TierOneHorizonDays))));
        Assert.False(GearLoadoutRules.GearEligible(Input(Shirt(new GearArrival.InDays(ForecastCoupled.TierOneHorizonDays + 1))), Shirt(new GearArrival.InDays(ForecastCoupled.TierOneHorizonDays + 1))));
        Assert.False(GearLoadoutRules.GearEligible(Input(Shirt(new GearArrival.NoKnownDay())), Shirt(new GearArrival.NoKnownDay())));
    }

    [Fact]
    public void AGearPatchIsPlacedOnASquareOrHasNoRoom()
    {
        var square = new Rectangle(1, 2, 3, 3);
        var patches = new GearPatches(new GearPatch.Placed(square), new GearPatch.NoRoom());
        // Only the placed patch takes cells from the warehouse.
        Assert.Equal(9, patches.Cells().Count);
        Assert.Empty(new GearPatches(new GearPatch.NoRoom(), new GearPatch.NoRoom()).Cells());
    }

    [Fact]
    public void AnEmptyCensusIsRecoveredWithNoDeficit()
    {
        var review = GearRules.ReviewGear(ColonyModelFixtures.NoGear() with { Pawns = [] });
        Assert.True(review.Recovered);
        Assert.Equal(0.0, review.Deficit);
        Assert.Empty(review.Loadouts);
        Assert.Empty(GearRules.GearReplacementNeeds(ColonyModelFixtures.NoGear() with { Pawns = [] }));
    }
}
