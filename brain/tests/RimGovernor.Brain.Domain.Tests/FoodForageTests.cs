using O = RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3668: the food Need designates the wild food plants its food plan opened. A naked start has no armed hunter (no hunt
// slots), so before this the hunt-only census left it nothing to designate and no colonist ate.
public sealed class FoodForageTests
{
    private static readonly AcquisitionSource Berries = new()
    {
        Observed = new() { Source = new() { Id = "Plant_Berry1" }, Token = "Plant_Berry1", Resource = "RawBerries", Yield = 10, Designation = new O.AcquisitionDesignation.Open(), Taken = false, Kind = new O.AcquisitionKind.Plant(true, 1, false) },
        Entity = new() { Id = "Plant_Berry1", DefName = "Plant_Berry", Label = "berry bush", Place = new O.EntityPlace.OnMap(1, new Cell(10, 10)) },
        NutritionYield = 0.5,
    };

    private static readonly AcquisitionSource Deer = new()
    {
        Observed = new() { Source = new() { Id = "Deer1" }, Token = "Deer1", Resource = "Deer", Yield = 1, Designation = new O.AcquisitionDesignation.Open(), Taken = false, Kind = new O.AcquisitionKind.Hunt(82, 1, false, false, false, false) },
        Entity = new() { Id = "Deer1", DefName = "Deer", Label = "deer", Place = new O.EntityPlace.OnMap(1, new Cell(30, 30)) },
        NutritionYield = 4.1,
    };

    // The plan opens the bush's forage channel and the deer's lone hunt.
    private static readonly FoodPlan Plan = new()
    {
        Portfolio =
        [
            new FoodPlanEntry { Channel = FoodPlanRules.ForageChannels([Berries])[0], Decision = FoodPlanDecision.Open },
            new FoodPlanEntry { Channel = CandidateRules.FoodCandidate(CandidateKind.Hunt, Deer.Id, new YieldSupply.Steady(Deer.NutritionYield), CandidateState.Closed, new CandidateLead.Days(0), new CandidateWork.PerDay(0)), Decision = FoodPlanDecision.Open },
        ],
    };

    private static IReadOnlyList<AcquisitionSource> Select(bool huntOnly, int slots)
    {
        var (sources, deficit) = RoundsAcquisitionPlanner.FoodPlanAcquisition(Plan, EquatableArray.From([Berries, Deer]));
        return RoundsAcquisitionPlanner.GoalSelection(sources, huntOnly, deficit, 0.0, food: true, new Dictionary<string, bool>(), slots);
    }

    [Fact]
    public void WithNoArmedHunterTheFoodNeedSelectsTheForageRow()
    {
        var selected = Select(huntOnly: false, slots: 0);

        Assert.Equal([Berries.Id], selected.Select(s => s.Id));
    }

    [Fact]
    public void ADesignatedForageBatchLeavesOnlyHuntsToPlan()
    {
        Assert.Empty(Select(huntOnly: true, slots: 0));
        Assert.Equal([Deer.Id], Select(huntOnly: true, slots: 1).Select(s => s.Id));
    }
}
