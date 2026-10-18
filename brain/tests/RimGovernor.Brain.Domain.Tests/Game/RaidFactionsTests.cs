using System.Runtime.CompilerServices;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3542: the Combat pawn groups of the faction defs, their kinds and the weapons each kind's tags select.
public sealed class RaidFactionsTests
{
    private static readonly DamageDef Bullet = new()
    {
        defName = "Bullet",
        defaultDamage = 12,
        armorCategory = new DamageArmorCategoryDef { defName = "Sharp" },
    };

    private static readonly ThingDef Projectile = new() { defName = "Bullet_Rifle", projectile = new ProjectileProperties { damageDef = Bullet } };

    private static class Members
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "verbs")]
        internal static extern ref List<VerbProperties>? Verbs(ThingDef def);
    }

    private static ThingDef Gun(string name, float commonality, params string[] tags)
    {
        var gun = new ThingDef
        {
            defName = name,
            weaponTags = [.. tags],
            generateCommonality = commonality,
            statBases =
            [
                new RimWorld.StatModifier { stat = new StatDef { defName = "RangedWeapon_Cooldown" }, value = 2 },
                new RimWorld.StatModifier { stat = new StatDef { defName = "RangedWeapon_DamageMultiplier" }, value = 1 },
                new RimWorld.StatModifier { stat = new StatDef { defName = "RangedWeapon_ArmorPenetrationMultiplier" }, value = 1 },
            ],
        };
        Members.Verbs(gun) = [new VerbProperties { verbClass = typeof(Verb_Shoot), defaultProjectile = Projectile, range = 25, burstShotCount = 1, warmupTime = 1 }];
        return gun;
    }

    private static readonly ThingDef Human = new() { defName = "Human" };
    private static readonly ThingDef Lancer = Gun("Mech_Lancer", 1);

    private static PawnGroupMaker Group(string kind, float commonality, params (PawnKindDef Kind, float Weight)[] options) => new()
    {
        kindDef = new PawnGroupKindDef { defName = kind },
        commonality = commonality,
        options = [.. options.Select(o => new PawnGenOption { kind = o.Kind, selectionWeight = o.Weight })],
    };

    private static PawnKindDef Kind(string name, float power, ThingDef race, params string[] tags) =>
        new() { defName = name, combatPower = power, race = race, weaponTags = tags.Length == 0 ? null : [.. tags] };

    private static DefinitionCatalog Catalog(params Def[] defs) =>
        new(
            [new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, Human, Lancer, Projectile, Gun("Rifle", 1, "Gun"), Gun("Revolver", 3, "Gun", "Sidearm"), .. defs],
            DefStatTable.Empty,
            new CatalogDerived("Silver", "Wort", 10f));

    [Fact]
    public void ACombatGroupDecodesItsKindsWeightsPowerAndTheCostCurve()
    {
        var pirates = new FactionDef
        {
            defName = "Pirate",
            maxPawnCostPerTotalPointsCurve = new SimpleCurve { new Verse.CurvePoint(0, 50), new Verse.CurvePoint(1000, 200) },
            pawnGroupMakers =
            [
                Group("Settlement", 100, (Kind("Guard", 40, Human, "Gun"), 1)),
                Group("Combat", 80, (Kind("Scavenger", 60, Human, "Gun"), 3), (Kind("Grenadier", 100, Human, "Gun", "Laser"), 1)),
            ],
        };

        var faction = Catalog(pirates).RaidFactions().Factions.Single();

        Assert.Equal("Pirate", faction.Def);
        Assert.Equal([new Policy.CurvePoint(0, 50), new Policy.CurvePoint(1000, 200)], faction.CostCurve);
        var group = faction.Groups.Single();
        Assert.Equal(80, group.Commonality);
        Assert.Equal(["Scavenger", "Grenadier"], group.Kinds.Select(k => k.Def));
        Assert.Equal([3.0, 1.0], group.Kinds.Select(k => k.Weight));
        Assert.Equal([60.0, 100.0], group.Kinds.Select(k => k.CombatPower));
    }

    [Fact]
    public void AKindsTagsSelectEveryDefThatCarriesThemAndReadTheirHit()
    {
        var pirates = new FactionDef { defName = "Pirate", pawnGroupMakers = [Group("Combat", 100, (Kind("Sniper", 80, Human, "Gun", "Sidearm"), 1))] };

        var kind = Catalog(pirates).RaidFactions().Factions.Single().Groups.Single().Kinds.Single();

        Assert.Equal(["Revolver", "Rifle"], kind.Weapons.Select(w => w.Def));
        Assert.Equal([3.0, 1.0], kind.Weapons.Select(w => w.Commonality));
        Assert.All(kind.Weapons, w => Assert.Equal(12, w.Weapon.Damage));
        Assert.All(kind.Weapons, w => Assert.Equal(ArmorCategory.Sharp, w.Weapon.ArmorCategory));
        Assert.Empty(kind.MissingTags);
    }

    [Fact]
    public void ATagNoWeaponCarriesIsMissingNotAnError()
    {
        var pirates = new FactionDef { defName = "Pirate", pawnGroupMakers = [Group("Combat", 100, (Kind("Grenadier", 100, Human, "Laser"), 1))] };

        var kind = Catalog(pirates).RaidFactions().Factions.Single().Groups.Single().Kinds.Single();

        Assert.Equal(["Laser"], kind.MissingTags);
        Assert.Empty(kind.Weapons);
    }

    [Fact]
    public void AKindWithNoWeaponTagsFightsWithItsRacesOwnAttacks()
    {
        var mechs = new FactionDef { defName = "Mechanoids", pawnGroupMakers = [Group("Combat", 100, (Kind("Mech_Lancer", 70, Lancer), 1))] };

        var kind = Catalog(mechs).RaidFactions().Factions.Single().Groups.Single().Kinds.Single();

        Assert.Equal(["Mech_Lancer"], kind.Weapons.Select(w => w.Def));
        Assert.True(kind.Weapons.Single().Weapon.Ranged);
    }
}
