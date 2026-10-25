using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3955: the party's mood effect is a plain read; a party whose memory repays nothing is never held.</summary>
public sealed class GatheringBenefitTests
{
    private static MoodPawn Colonist(string id) => MoodFixtures.Has(id, .5, .5);

    private static GatheringInput Input(double benefit) => new()
    {
        Ledger = new MoodLedger { Pawns = Enumerable.Range(0, 4).Select(_ => new MoodPawnLoss { Lost = [new MoodThought("Hungry", -5)] }).ToList() },
        Pawns = EquatableArray.From(Enumerable.Range(0, 4).Select(i => Colonist("p" + i))),
        Benefit = benefit,
        Calm = true,
        Census = new CurrentConstruction
        {
            Buildings = [ConstructionObservationFixtures.Built(new Building(PartySpotRules.PartySpotDefinition, new Cell(1, 1), Rotation.North, ""), id: "spot", cells: [new Cell(1, 1)])],
            Sites = [],
            Deficit = new Dictionary<Resource, long>(),
        },
    };

    [Fact]
    public void AMemoryThatRepaysNothingStartsNoParty()
    {
        Assert.IsType<GatheringPlan.NoParty>(GatheringRules.PlanGathering(Input(0)));
    }

    [Fact]
    public void APositiveMemoryStartsTheParty()
    {
        var plan = Assert.IsType<GatheringPlan.Ready>(GatheringRules.PlanGathering(Input(2)));
        Assert.Equal(new PawnId("p0"), plan.Organizer);
        Assert.True(GatheringRules.GatheringOwed(plan));
    }

    [Fact]
    public void NoPartySatisfiesTheGatheringNeed()
    {
        Assert.False(GatheringRules.GatheringOwed(new GatheringPlan.NoParty()));
        Assert.Equal(Satisfaction.Met, RoomsFamily.GatheringsNeed(new RoomsFacts { Gathering = new GatheringPlan.NoParty() }, false).Satisfaction);
    }

    [Fact]
    public void MissingCensusCannotBecomeNoPartyEvenWhenConditionsRefuse()
    {
        Assert.Throws<RequiredFactException>(() => GatheringRules.PlanGathering(Input(0) with { Ledger = null! }));
        Assert.Throws<RequiredFactException>(() => GatheringRules.PlanGathering(Input(0) with { Census = null! }));
        Assert.Throws<RequiredFactException>(() => RoomsFamily.GatheringsNeed(new RoomsFacts(), false));
    }

    [Fact]
    public void ReadyRequiresAnOrganizer()
    {
        Assert.Throws<ArgumentException>(() => new GatheringPlan.Ready(default));
    }

    [Fact]
    public void NativePartyGatesYieldNoParty()
    {
        var input = Input(2);
        Assert.IsType<GatheringPlan.NoParty>(GatheringRules.PlanGathering(input with { Calm = false }));
        Assert.IsType<GatheringPlan.NoParty>(GatheringRules.PlanGathering(input with { Pawns = [Colonist("p0")] }));
        Assert.IsType<GatheringPlan.NoParty>(GatheringRules.PlanGathering(input with { Census = input.Census with { Buildings = [] } }));
        Assert.IsType<GatheringPlan.NoParty>(GatheringRules.PlanGathering(input with { Ledger = new MoodLedger() }));
        Assert.IsType<GatheringPlan.NoParty>(GatheringRules.PlanGathering(input with { Pawns = EquatableArray.From(input.Pawns.Select(p => p with { Pawn = p.Pawn with { Downed = true } })) }));
        Assert.IsType<GatheringPlan.NoParty>(GatheringRules.PlanGathering(input with { Pawns = EquatableArray.From(input.Pawns.Select(p => p with { Pawn = p.Pawn with { MentalState = new RimGovernor.Contracts.Wire.Observations.PawnMentalState { DefName = "Wander", IsAggro = false, Ticks = 120 } } })) }));
        Assert.IsType<GatheringPlan.NoParty>(GatheringRules.PlanGathering(input with { Pawns = EquatableArray.From(input.Pawns.Select(p => p with { Thoughts = [new MoodThought(GatheringRules.PartyThought, 2)] })) }));
    }
}
