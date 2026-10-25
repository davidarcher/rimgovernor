using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class GearCensusTests
{
    [Fact]
    public void GearPolicyAndModelUseTheSameActualPawnIdeology()
    {
        var row = EmergencyFixtures.Placed("pawn");
        row = row with
        {
            Settings = row.Settings with
            {
                PolicyInputs = row.Settings.PolicyInputs with
                {
                    Ideo = new O.PawnIdeo.Member("ideo", ["Nudism_Preferred"], null, 1),
                },
            },
        };
        var gear = ColonyModelFixtures.NoGear() with
        {
            Pawns = [new GearPawn { Pawn = new("pawn"), Policy = new(), LoadoutModel = new GearModel.Modeled(new GearLoadoutInput { Threat = new RaidThreat.Uncomposed() }) }],
        };
        var pawns = new O.PawnSnapshot { Context = ColonyFactsCoreTests.Snapshot().Context, Pawns = [row] };

        var stamped = Assert.Single(ColonyGearReads.StampGearCreepjoiners(gear, pawns, new()).Pawns);

        Assert.Equal(["Nudism_Preferred"], stamped.Policy!.Role.Precepts);
        Assert.Equal(["Nudism_Preferred"], Assert.IsType<GearModel.Modeled>(stamped.LoadoutModel).Input.Role.Precepts);
    }

    [Fact]
    public void GearRolesRequireTheActualPawnCensusEvenWhenNoOutfitIsAssigned()
    {
        var gear = ColonyModelFixtures.NoGear() with
        {
            Pawns = [new GearPawn { Pawn = new("missing"), LoadoutModel = new GearModel.NoOutfit() }],
        };
        Assert.Throws<BridgeContractException>(() => ColonyGearReads.StampGearCreepjoiners(gear, null, new()));
        var pawns = new O.PawnSnapshot { Context = ColonyFactsCoreTests.Snapshot().Context, Pawns = [] };
        Assert.Throws<BridgeContractException>(() => ColonyGearReads.StampGearCreepjoiners(gear, pawns, new()));
    }

    [Fact]
    public void AnObservedEmptyPawnAndGearCensusHasNoMissingRoles()
    {
        var pawns = new O.PawnSnapshot { Context = ColonyFactsCoreTests.Snapshot().Context, Pawns = [] };
        Assert.Empty(ColonyGearReads.StampGearCreepjoiners(ColonyModelFixtures.NoGear(), pawns, new()).Pawns);
    }
}
