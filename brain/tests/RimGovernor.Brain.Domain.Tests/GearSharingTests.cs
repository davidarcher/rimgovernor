using D = RimGovernor.Contracts.Wire.Defs;
using O = RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class GearSharingTests
{
    private static GearOption Shirt(string stuff, double cold) => new() { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("Shirt" + stuff, "Shirt", stuff, GearSource.Bill)), Slot = GearSlot.SkinTorso, Layers = ["OnSkin"], Groups = ["Torso"], Cold = cold, Cost = 20, Sharp = 0.2 };

    // Each call builds fresh rows, so equal censuses are distinct instances, as each planner's copy is.
    private static List<GearPawn> Rows(string pawn, double ambient) =>
    [
        new GearPawn { Observed = GearFixtures.Loadout("") with { Token = "load", Pawn = new(pawn) }, LoadoutModel = new GearModel.Modeled(new GearLoadoutInput { Observed = GearFixtures.Loadout(""), Body = EmergencyFixtures.Placed("").Settings.Body with { ComfortableMinC = 10, ComfortableMaxC = 30 }, Threat = ColonyModelFixtures.NoThreat, Ambient = ambient, Options = [Shirt("Cloth", 5), Shirt("Wool", 15)], Role = GearFixtures.Role }) },
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
