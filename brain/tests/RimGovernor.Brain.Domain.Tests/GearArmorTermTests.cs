using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3545: the gear planner values armor as damage per hit avoided against the colony's raid, from the same ArmorResponse code the ledger reads.
public sealed class GearArmorTermTests
{
    private static GearOption Garment(string id, GearSlot slot, string layer, double sharp, double blunt = 0, int quality = 2) =>
        new() { ID = id, Definition = new(id), Slot = slot, Layers = [layer], Groups = ["Torso"], Quality = quality, Condition = 1, Sharp = sharp, Blunt = blunt, Source = GearSource.Stored };

    private static readonly GearOption Cloth = Garment("cloth-duster", GearSlot.Outer, "Shell", 0.1, 0.1);
    private static readonly GearOption Devilstrand = Garment("devilstrand-duster", GearSlot.Outer, "Shell", 0.5, 0.3);
    private static readonly GearOption Flak = Garment("flak-vest", GearSlot.MiddleTorso, "Middle", 0.9, 0.4);

    private static Fact<RaidThreat> Threat(ArmorCategory category) => Known(new RaidThreat
    {
        Bins = [new WeaponBin { Damage = 12, ArmorPenetration = 0.05, Category = category, Weight = 1 }],
        Coverage = new BodyCoverage { Shares = [new CoverageShare { Groups = ["Torso"], Probability = 1 }], Layers = ["OnSkin", "Middle", "Shell"] },
    });

    private static double Value(Fact<RaidThreat> threat, GearOption option) => new GearArmorTerm(threat).Of(option);

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

        Assert.True(Value(threat, Devilstrand with { Quality = 6 }) > Value(threat, Devilstrand));
        Assert.True(Value(threat, Devilstrand with { Condition = 0.4 }) < Value(threat, Devilstrand));
    }

    [Fact]
    public void AnUnknownThreatStillPrefersDevilstrandToClothAndReportsTheFinding()
    {
        var unknown = Unknown<RaidThreat>();
        Assert.True(Value(unknown, Devilstrand) > Value(unknown, Cloth));

        var loadout = GearLoadoutRules.PlanGearLoadout(new GearLoadoutInput { ComfortableMin = 10, ComfortableMax = 30, Ambient = 20, Options = [Cloth, Devilstrand] });
        Assert.Equal([GearArmorTerm.ThreatUnknownFinding], loadout.Findings);
        Assert.Contains(loadout.Target, o => o.ID == Devilstrand.ID);

        var known = GearLoadoutRules.PlanGearLoadout(new GearLoadoutInput { ComfortableMin = 10, ComfortableMax = 30, Ambient = 20, Threat = Threat(ArmorCategory.Sharp), Options = [Cloth, Devilstrand] });
        Assert.Empty(known.Findings);
        Assert.Contains(known.Target, o => o.ID == Devilstrand.ID);
    }

    [Fact]
    public void ASoldierWeighsTheSameArmorTenTimesAnotherPawn()
    {
        var threat = Threat(ArmorCategory.Sharp);
        GearLoadout Plan(bool soldier) => GearLoadoutRules.PlanGearLoadout(new GearLoadoutInput
        {
            ComfortableMin = 10, ComfortableMax = 30, Ambient = 20, Threat = threat, Options = [Flak],
            Role = new GearRoleInput { DraftedSquad = soldier },
        });

        var armor = Value(threat, Flak);
        Assert.Equal(armor * 10, Plan(true).Score - Plan(false).Score + armor, 9);
    }
}
