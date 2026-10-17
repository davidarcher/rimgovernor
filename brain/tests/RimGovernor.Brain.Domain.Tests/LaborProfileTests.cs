using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Labor profiles are keyed by filing key (#3231 decision 22): every Need whose Concern carried a profile keeps
/// it under its NeedId, and an unmigrated Concern keeps its own.
/// </summary>
public sealed class LaborProfileTests
{
    public static TheoryData<string, WorkType[]> Restored => new()
    {
        { HomeUpkeepFamily.Clearance.Value, [WorkType.Construction] },
        { HomeUpkeepFamily.Repairs.Value, [WorkType.Construction] },
        { MilitaryFamily.Defense.Value, [WorkType.Construction] },
        { MilitaryFamily.Training.Value, [WorkType.Construction] },
        { MilitaryFamily.Equipment.Value, [WorkType.Construction, WorkType.Tailoring, WorkType.Smithing, WorkType.Crafting] },
        { RoomsFamily.Comfort.Value, [WorkType.Construction] },
        { RoomsFamily.Lighting.Value, [WorkType.Construction] },
        { RoomsFamily.Flooring.Value, [WorkType.Construction] },
        { RoomsFamily.Art.Value, [WorkType.Art] },
        { LayoutFamily.StoneShell.Value, [WorkType.Construction] },
        { LayoutFamily.HomeCoverage.Value, [WorkType.Construction] },
        { LayoutFamily.ClimateEnvelope.Value, [WorkType.Construction] },
        { LayoutFamily.Firebreak.Value, [WorkType.PlantCutting, WorkType.Construction] },
        { FoodStoresRules.FoodStorageNeed.Value, [WorkType.Construction] },
        { FoodRules.BlightNeed.Value, [WorkType.PlantCutting] },
        { FoodRules.BabyFeedingNeed.Value, [WorkType.Cooking] },
        { AnimalsFamily.Containment.Value, [WorkType.Construction] },
        { AnimalsFamily.Herd.Value, [WorkType.Handling] },
        { EconomyFamily.Research.Value, [WorkType.Research] },
        { EconomyFamily.Resource.Value, [WorkType.Mining, WorkType.PlantCutting, WorkType.Crafting] },
        { EconomyFamily.Trade.Value, [WorkType.Art, WorkType.Tailoring, WorkType.Smithing, WorkType.Crafting] },
        { AdminFamily.Burial.Value, [WorkType.Construction] },
        { AdminFamily.Stockpiles.Value, [WorkType.Hauling] },
        { MedicalRules.MedicalReservesNeed.Value, [WorkType.Crafting, WorkType.PlantCutting] },
        { ShelterFamily.Starter.Value, [WorkType.Construction] },
        { ShelterFamily.Sleeping.Value, [WorkType.Construction] },
        { ShelterFamily.Expansion.Value, [WorkType.Construction] },
    };

    [Theory]
    [MemberData(nameof(Restored))]
    public void EachMigratedNeedKeepsItsConcernsProfileUnderItsNeedId(string need, WorkType[] profile)
    {
        var id = new NeedId(need);
        Assert.True(id.IsValid);
        Assert.Equal(profile, LaborRules.NeedLabor(id));
        Assert.Equal(profile, LaborRules.ConcernLabor(id.FilingKey));
    }

    [Fact]
    public void ASubjectNeedSharesItsKindsProfile()
    {
        Assert.Equal([WorkType.Handling], LaborRules.NeedLabor(NeedId.Of(AnimalsFamily.FamilyId, "herd", "pawn-1")));
        Assert.Empty(LaborRules.NeedLabor(AdminFamily.CreepJoiners));
        Assert.Equal([WorkType.Construction], LaborRules.NeedLabor(LayoutFamily.Routes));
    }
}
