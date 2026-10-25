using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3545: the gear planner values armor as damage per hit avoided against the colony's raid, from the same ArmorResponse code the ledger reads.
public sealed class GearArmorTermTests
{
    private static GearOption Garment(string id, GearSlot slot, string layer, double sharp, double blunt = 0, int quality = 2) =>
        new() { Origin = new GearOptionOrigin.Observed(GearFixtures.Option(id, id, "", GearSource.Stored) with { Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded((RimGovernor.Brain.GameFacts.Defs.QualityCategory)(quality)) }), Slot = slot, Layers = [layer], Groups = ["Torso"], Sharp = sharp, Blunt = blunt };

    private static readonly GearOption Cloth = Garment("cloth-duster", GearSlot.Outer, "Shell", 0.1, 0.1);
    private static readonly GearOption Devilstrand = Garment("devilstrand-duster", GearSlot.Outer, "Shell", 0.5, 0.3);
    private static readonly GearOption Flak = Garment("flak-vest", GearSlot.MiddleTorso, "Middle", 0.9, 0.4);

    private static RaidThreat.Composed Threat(ArmorCategory category) => new RaidThreat.Composed(
        [new WeaponBin { Damage = 12, ArmorPenetration = 0.05, Category = category, Weight = 1 }],
        new BodyCoverage { Shares = [new CoverageShare { Groups = ["Torso"], Probability = 1 }], Layers = ["OnSkin", "Middle", "Shell"] },
        new NaturalArmor());

    private static double Value(RaidThreat threat, GearOption option) => new GearArmorTerm(threat).Of(option);

    [Fact]
    public void AgainstASharpHeavyRaidDevilstrandBeatsClothAndAFlakVestBeatsBothOnTheTorso()
    {
        var threat = Threat(ArmorCategory.Sharp);

        Assert.True(Value(threat, Devilstrand) > Value(threat, Cloth));
        Assert.True(Value(threat, Flak) > Value(threat, Devilstrand));
        Assert.True(Value(threat, Cloth) > 0);
    }

    [Fact]
    public void AgainstABluntOnlyRaidTheRankingFollowsTheBluntStat()
    {
        var threat = Threat(ArmorCategory.Blunt);
        var sharpOnly = Garment("sharp-only", GearSlot.Outer, "Shell", 0.9, 0);

        Assert.Equal(0, Value(threat, sharpOnly), 9);
        Assert.True(Value(threat, Devilstrand) > Value(threat, Cloth));
        Assert.True(Value(threat, Flak) > Value(threat, Devilstrand));
        Assert.True(Value(threat, Cloth) > Value(threat, sharpOnly));
    }

    [Fact]
    public void QualityAndConditionEnterThroughTheArmorResponse()
    {
        var threat = Threat(ArmorCategory.Sharp);

        Assert.True(Value(threat, Devilstrand with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Devilstrand).Origin).Value) with { Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Legendary) }) }) > Value(threat, Devilstrand));
        Assert.True(Value(threat, Devilstrand with { Origin = new GearOptionOrigin.Observed((((GearOptionOrigin.Observed)(Devilstrand).Origin).Value) with { Condition = 0.4 }) }) < Value(threat, Devilstrand));
    }

    [Fact]
    public void AnUncomposedThreatStillPrefersDevilstrandToClothAndReportsTheFinding()
    {
        var uncomposed = ColonyModelFixtures.NoThreat;
        Assert.True(Value(uncomposed, Devilstrand) > Value(uncomposed, Cloth));

        var loadout = GearLoadoutRules.PlanGearLoadout(new GearLoadoutInput { Observed = GearFixtures.Loadout(""), Body = EmergencyFixtures.Placed("").Settings.Body with { ComfortableMinC = 10, ComfortableMaxC = 30 }, Threat = ColonyModelFixtures.NoThreat, Ambient = 20, Options = [Cloth, Devilstrand], Role = GearFixtures.Role });
        Assert.Equal([GearArmorTerm.ThreatUncomposedFinding], loadout.Findings);
        Assert.Contains(loadout.Target, o => o.ID == Devilstrand.ID);

        var known = GearLoadoutRules.PlanGearLoadout(new GearLoadoutInput { Observed = GearFixtures.Loadout(""), Body = EmergencyFixtures.Placed("").Settings.Body with { ComfortableMinC = 10, ComfortableMaxC = 30 }, Ambient = 20, Threat = Threat(ArmorCategory.Sharp), Options = [Cloth, Devilstrand], Role = GearFixtures.Role });
        Assert.Empty(known.Findings);
        Assert.Contains(known.Target, o => o.ID == Devilstrand.ID);
    }

    [Fact]
    public void ASoldierWeighsTheSameArmorTenTimesAnotherPawn()
    {
        var threat = Threat(ArmorCategory.Sharp);
        GearLoadout Plan(bool soldier) => GearLoadoutRules.PlanGearLoadout(new GearLoadoutInput { Observed = GearFixtures.Loadout(""), Body = EmergencyFixtures.Placed("").Settings.Body with { ComfortableMinC = 10, ComfortableMaxC = 30 }, Ambient = 20, Threat = threat, Options = [Flak], Role = new GearRoleInput { Observed = GearFixtures.Loadout("") with { ApparelPolicy = PoliciesTests.EmptyOutfit }, Pawn = EmergencyFixtures.Placed("") with { Drafted = soldier } } });

        var armor = Value(threat, Flak);
        Assert.Equal(armor * 10, Assert.Single(Plan(true).Gaps).Gain - Assert.Single(Plan(false).Gaps).Gain + armor, 9);
    }
}
