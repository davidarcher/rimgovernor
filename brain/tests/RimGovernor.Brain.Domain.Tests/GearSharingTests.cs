using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class GearSharingTests
{
    private static GearOption Shirt(string stuff, double cold) => new()
    {
        ID = "Shirt" + stuff,
        Definition = new("Shirt"),
        Stuff = new(stuff),
        Slot = GearSlot.SkinTorso,
        Layers = ["OnSkin"],
        Groups = ["Torso"],
        Source = GearSource.Bill,
        Quality = 2,
        Condition = 1,
        Cold = cold,
        Cost = 20,
        Sharp = 0.2,
    };

    // Each call builds fresh rows, so equal censuses are distinct instances, as each planner's copy is.
    private static List<GearPawn> Rows(string pawn, double ambient) =>
    [
        new GearPawn
        {
            Pawn = new(pawn),
            Loadout = "load",
            LoadoutModel = new GearModel.Modeled(new GearLoadoutInput { Threat = ColonyModelFixtures.NoThreat, Ambient = ambient, ComfortableMin = 10, ComfortableMax = 30, Options = [Shirt("Cloth", 5), Shirt("Wool", 15)] }),
        },
    ];

    [Fact]
    public void EqualRowsShareOnePlanAndChangedRowsPlanAgain()
    {
        var pawn = "wearer" + Guid.NewGuid().ToString("N");
        var first = GearRules.SharedColonyGear(Rows(pawn, 20), out var sharedFirst);
        var copy = GearRules.SharedColonyGear(Rows(pawn, 20), out var sharedCopy);
        var changed = GearRules.SharedColonyGear(Rows(pawn, -10), out var sharedChanged);

        Assert.False(sharedFirst);
        Assert.True(sharedCopy);
        Assert.Same(first, copy);
        Assert.False(sharedChanged);
        Assert.Equal(GearLoadoutRules.PlanColonyGear(Rows(pawn, -10)), changed);
    }
}
