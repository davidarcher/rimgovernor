using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3542: the expected composition of a ground raid from the hostile factions' combat groups.
public sealed class RaidCompositionTests
{
    private static readonly WeaponDef Rifle = new() { Ranged = true, Damage = 12, AP = 0.18, ArmorCategory = ArmorCategory.Sharp };
    private static readonly WeaponDef Club = new() { Melee = true, Damage = 10, AP = 0.15, ArmorCategory = ArmorCategory.Blunt };

    private static RaidKind Kind(string def, double power, WeaponDef weapon, double weight = 1) =>
        new() { Def = def, Weight = weight, CombatPower = power, Weapons = [new RaidWeapon { Def = def + "Gun", Weapon = weapon }] };

    private static RaidTable Table(params RaidFaction[] factions) => new() { Factions = EquatableArray.From(factions) };

    private static RaidFaction Faction(string def, params RaidKind[] kinds) =>
        new() { Def = def, Groups = [new RaidPawnGroup { Kinds = EquatableArray.From(kinds) }] };

    private static readonly RaidFaction Pirates = Faction("Pirate", Kind("Scavenger", 60, Rifle), Kind("Thug", 100, Club));

    [Fact]
    public void FourHundredPointsOfSixtyAndOneHundredPowerKindsIsAboutFivePawnsWeightedBySelection()
    {
        var composition = RaidCompositionRules.Of(400, ["Pirate"], Table(Pirates));

        Assert.Equal(5, composition.Pawns.Value, 9);
        Assert.Equal(1, composition.Bins.Sum(b => b.Weight), 9);
        Assert.Equal(0.5, composition.Bins.Single(b => b.Category == ArmorCategory.Sharp).Weight, 9);
        Assert.Equal(0.5, composition.Bins.Single(b => b.Category == ArmorCategory.Blunt).Weight, 9);
        Assert.Empty(composition.Findings);
    }

    [Fact]
    public void BinWeightsFollowTheKindSelectionWeights()
    {
        var table = Table(Faction("Pirate", Kind("Scavenger", 60, Rifle, weight: 3), Kind("Thug", 100, Club, weight: 1)));

        var composition = RaidCompositionRules.Of(400, ["Pirate"], table);

        Assert.Equal(0.75, composition.Bins.Single(b => b.Category == ArmorCategory.Sharp).Weight, 9);
        Assert.Equal(ArmorCategory.Blunt, composition.Bins.Single(b => b.Category == ArmorCategory.Blunt).Category);
    }

    [Fact]
    public void TheCostCurveKeepsKindsDearerThanTheCapOutOfTheRaid()
    {
        var capped = Pirates with { CostCurve = [new CurvePoint(0, 70), new CurvePoint(1000, 70)] };

        var composition = RaidCompositionRules.Of(420, ["Pirate"], Table(capped));

        Assert.Equal(7, composition.Pawns.Value, 9);
        Assert.Equal(1, composition.Bins.Single().Weight, 9);
        Assert.Equal(ArmorCategory.Sharp, composition.Bins.Single().Category);
    }

    [Fact]
    public void AFactionWithNoCombatGroupMakesTheCompositionUnknownWithAFinding()
    {
        var composition = RaidCompositionRules.Of(400, ["Pirate", "Traders"], Table(Pirates, new RaidFaction { Def = "Traders" }));

        Assert.False(composition.Pawns.IsKnown);
        Assert.Empty(composition.Bins);
        Assert.Equal(["raid_faction_no_combat_group:Traders"], composition.Findings);
    }

    [Fact]
    public void NoHostileFactionIsAnUnknownCompositionWithAFinding()
    {
        var composition = RaidCompositionRules.Of(400, [], Table(Pirates));

        Assert.False(composition.Pawns.IsKnown);
        Assert.Equal(["raid_no_hostile_factions"], composition.Findings);
    }

    [Fact]
    public void AWeaponTagNoDefCarriesIsAFindingAndTheKindKeepsItsOtherWeapons()
    {
        var kind = Kind("Scavenger", 60, Rifle) with { MissingTags = ["Laser"] };

        var composition = RaidCompositionRules.Of(120, ["Pirate"], Table(Faction("Pirate", kind)));

        Assert.True(composition.Pawns.IsKnown);
        Assert.Equal(["raid_weapon_tag_unmatched:Laser"], composition.Findings);
    }

    [Fact]
    public void AKindWhoseTagsSelectNoWeaponDropsOutOfTheBinsButStillCountsAsPawns()
    {
        var bare = new RaidKind { Def = "Bare", Weight = 1, CombatPower = 60, MissingTags = ["Laser"] };

        var composition = RaidCompositionRules.Of(240, ["Pirate"], Table(Faction("Pirate", bare, Kind("Thug", 100, Club))));

        Assert.Equal(3, composition.Pawns.Value, 9);
        Assert.Equal(1, composition.Bins.Single().Weight, 9);
        Assert.Contains("raid_kind_no_weapon:Bare", composition.Findings);
    }

    [Fact]
    public void WeaponsOfTheSameKindShareItsWeightByCommonality()
    {
        var kind = new RaidKind
        {
            Def = "Raider",
            Weight = 1,
            CombatPower = 60,
            Weapons = [new RaidWeapon { Def = "Rifle", Commonality = 3, Weapon = Rifle }, new RaidWeapon { Def = "Club", Commonality = 1, Weapon = Club }],
        };

        var composition = RaidCompositionRules.Of(60, ["Pirate"], Table(Faction("Pirate", kind)));

        Assert.Equal(0.75, composition.Bins.Single(b => b.Category == ArmorCategory.Sharp).Weight, 9);
    }

    [Fact]
    public void SeveralHostileFactionsRaidWithEqualWeight()
    {
        var tribe = Faction("Tribe", Kind("Hunter", 100, Club));

        var composition = RaidCompositionRules.Of(400, ["Pirate", "Tribe"], Table(Pirates, tribe));

        Assert.Equal((5 + 4) / 2.0, composition.Pawns.Value, 9);
        Assert.Equal(1.25 / 4.5, composition.Bins.Single(b => b.Category == ArmorCategory.Sharp).Weight, 9);
    }
}
