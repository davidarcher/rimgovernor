using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

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
                    Ideo = new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.Member("ideo", ["Nudism_Preferred"], null, 1),
                },
            },
        };
        var gear = ColonyModelFixtures.NoGear() with
        {
            Pawns = [new GearPawn { Observed = GearFixtures.Loadout("") with { Token = "", Pawn = new("pawn") }, Policy = new() { Observed = PoliciesTests.EmptyOutfit, Role = GearFixtures.Role }, LoadoutModel = new GearModel.Modeled(new GearLoadoutInput { Observed = GearFixtures.Loadout(""), Body = EmergencyFixtures.Placed("").Settings.Body with { ComfortableMinC = 0, ComfortableMaxC = 0 }, Threat = new RaidThreat.Uncomposed(), Role = GearFixtures.Role }) }],
        };
        var pawns = new RimGovernor.Brain.GameFacts.Observations.PawnSnapshot { Context = ColonyFactsCoreTests.Snapshot().Context, Pawns = [row] };

        var stamped = Assert.Single(ColonyGearReads.StampGearCreepjoiners(gear, pawns, new()).Pawns);

        Assert.Equal(["Nudism_Preferred"], stamped.Policy!.Role.Precepts);
        Assert.Equal(["Nudism_Preferred"], Assert.IsType<GearModel.Modeled>(stamped.LoadoutModel).Input.Role.Precepts);
    }

    [Fact]
    public void GearRolesRequireTheActualPawnCensusEvenWhenNoOutfitIsAssigned()
    {
        var gear = ColonyModelFixtures.NoGear() with
        {
            Pawns = [new GearPawn { Observed = GearFixtures.Loadout("") with { Token = "", Pawn = new("missing") }, LoadoutModel = new GearModel.NoOutfit() }],
        };
        Assert.Throws<BridgeContractException>(() => ColonyGearReads.StampGearCreepjoiners(gear, null, new()));
        var pawns = new RimGovernor.Brain.GameFacts.Observations.PawnSnapshot { Context = ColonyFactsCoreTests.Snapshot().Context, Pawns = [] };
        Assert.Throws<BridgeContractException>(() => ColonyGearReads.StampGearCreepjoiners(gear, pawns, new()));
    }

    [Fact]
    public void AnObservedEmptyPawnAndGearCensusHasNoMissingRoles()
    {
        var pawns = new RimGovernor.Brain.GameFacts.Observations.PawnSnapshot { Context = ColonyFactsCoreTests.Snapshot().Context, Pawns = [] };
        Assert.Empty(ColonyGearReads.StampGearCreepjoiners(ColonyModelFixtures.NoGear(), pawns, new()).Pawns);
    }
}
