using static RimGovernor.Brain.Domain.Fact;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3955: the party's mood effect is a plain read; a party whose memory repays nothing is never held.</summary>
public sealed class GatheringBenefitTests
{
    private static MoodPawn Colonist(string id) => new() { Id = new PawnId(id), Mood = new MoodLevel.Has(0.5, 0.5) };

    private static GatheringInput Input(double benefit) => new()
    {
        Ledger = new MoodLedger { Pawns = Enumerable.Range(0, 4).Select(_ => new MoodPawnLoss { Lost = [new MoodThought("Hungry", -5)] }).ToList() },
        Pawns = EquatableArray.From(Enumerable.Range(0, 4).Select(i => Colonist("p" + i))),
        Benefit = benefit,
        Calm = Known(true),
        Census = new CurrentConstruction
        {
            Buildings = [new CurrentBuilding { Id = "spot", Building = new Building(PartySpotRules.PartySpotDefinition, new Cell(1, 1), Rotation.North, ""), Cells = [new Cell(1, 1)] }],
            Sites = [],
            Deficit = new Dictionary<Resource, long>(),
        },
    };

    [Fact]
    public void AMemoryThatRepaysNothingStartsNoParty()
    {
        Assert.True(GatheringRules.PlanGathering(Input(0)).TryGetValue(out var plan));
        Assert.True(plan.Organizer.IsEmpty);
    }

    [Fact]
    public void APositiveMemoryStartsTheParty()
    {
        Assert.True(GatheringRules.PlanGathering(Input(2)).TryGetValue(out var plan));
        Assert.False(plan.Organizer.IsEmpty);
    }
}
