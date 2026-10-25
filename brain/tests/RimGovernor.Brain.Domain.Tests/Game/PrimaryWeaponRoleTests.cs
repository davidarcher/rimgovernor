using System.Runtime.CompilerServices;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3562: the exposure role comes from the primary weapon the gear census already carries (equipment + the frame's things table),
// decoded against the catalog by the real observation read, not from an injected Primary item.
public sealed class PrimaryWeaponRoleTests
{
    private static readonly DamageDef Bullet = new()
    {
        defName = "Bullet",
        defaultDamage = 12,
        armorCategory = new DamageArmorCategoryDef { defName = "Sharp" },
    };

    private static readonly ThingDef Projectile = new() { defName = "Bullet_Revolver", projectile = new ProjectileProperties { damageDef = Bullet } };

    private static class Members
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "verbs")]
        internal static extern ref List<VerbProperties>? Verbs(ThingDef def);
    }

    private static ThingDef Revolver()
    {
        var gun = new ThingDef
        {
            defName = "Gun_Revolver",
            category = Verse.ThingCategory.Item,
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

    private static DefinitionCatalog Catalog(ThingDef? gun = null) =>
        new(
            [new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, Projectile, gun ?? Revolver(), new ThingDef { defName = "MeleeWeapon_Knife" }],
            DefStatTable.Empty,
            new CatalogDerived("Silver", "Wort", 10f));

    // The census of one colonist wielding <paramref name="def"/> (or unarmed), decoded by the observation read.
    private static GearObservation Census(string? def, DefinitionCatalog? catalog = null)
    {
        var equipment = GearFixtures.NoEquipment;
        var things = new List<RimGovernor.Brain.GameFacts.Observations.Thing>();
        if (def is not null)
        {
            const string id = "Weapon_1";
            equipment = equipment with { Armed = true, PrimaryId = id, Equipped = [GearFixtures.Item(id, new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Good)) with { ConditionFraction = 0.8 }] };
            things.Add(new RimGovernor.Brain.GameFacts.Observations.Thing { Entity = new RimGovernor.Brain.GameFacts.Observations.EntityRef { Id = id, DefName = def, Label = def, Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.Unplaced() }, Forbidden = false, Corpse = false });
        }
        var tables = new Tables(default, default, Things.Of(things), []);
        var row = GearFixtures.Loadout("pawn") with { ApparelPolicy = PoliciesTests.EmptyOutfit };
        var defs = new GearDefinitions { Catalog = catalog ?? Catalog(), Finished = new HashSet<string>() };
        var model = ColonyGearReads.GearLoadoutModelFacts(defs, tables, 20, row, new RimGovernor.Brain.Policy.ApparelPolicyState() { Observed = PoliciesTests.EmptyOutfit, Role = new GearRoleInput { Observed = GearFixtures.Loadout("") with { ApparelPolicy = PoliciesTests.EmptyOutfit }, Pawn = EmergencyFixtures.Placed("") with { Equipment = equipment } } }, ColonyModelFixtures.NoThreat, new StuffArrival(new Dictionary<Resource, long>(), []));
        return ColonyModelFixtures.NoGear() with
        {
            Stored = [],
            Pawns = [new GearPawn { Observed = GearFixtures.Loadout("") with { Token = "load", Pawn = new("pawn") }, Candidates = [], Apparel = [], LoadoutModel = model }],
        };
    }

    private static string Role(string? def)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0) with { Gear = Census(def) };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context;
        return HarmLedger.RoleOf(context.Outfits["pawn"]);
    }

    [Fact]
    public void ARevolverInTheObservedEquipmentMakesTheColonistAGunner() => Assert.Equal(ExposureRoles.Gunner, Role("Gun_Revolver"));

    [Fact]
    public void AMeleeWeaponMakesTheColonistAMeleeFighter() => Assert.Equal(ExposureRoles.Melee, Role("MeleeWeapon_Knife"));

    [Fact]
    public void AnUnarmedColonistIsNoncombatant() => Assert.Equal(ExposureRoles.Noncombatant, Role(null));

    [Fact]
    public void JoinerWeaponTraitsReceiveTheProductionCatalogThroughTheEquipmentRead()
    {
        var model = Assert.IsType<GearModel.Modeled>(Assert.Single(Census("Gun_Revolver").Pawns).LoadoutModel);
        var primary = Assert.Single(model.Input.Worn, o => o.Slot == GearSlot.Primary);
        Assert.True(primary.Weapon.TraitRanged);
        Assert.False(primary.Weapon.TraitIncendiary);
        Assert.Equal(Catalog().WeaponOf("Gun_Revolver"), primary.Weapon);
    }

    [Fact]
    public void ABeamIncendiaryVerbReachesTheJoinerWithoutUsingTheCombatProjectileFlag()
    {
        var gun = Revolver();
        Members.Verbs(gun)![0].ai_BeamIsIncendiary = true;
        var model = Assert.IsType<GearModel.Modeled>(Assert.Single(Census("Gun_Revolver", Catalog(gun)).Pawns).LoadoutModel);
        var primary = Assert.Single(model.Input.Worn, o => o.Slot == GearSlot.Primary);
        Assert.True(primary.Weapon.TraitIncendiary);
        Assert.False(primary.Weapon.Incendiary);
    }
}
