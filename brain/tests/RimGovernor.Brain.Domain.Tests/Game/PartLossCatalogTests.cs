using System.Runtime.CompilerServices;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using RimWorld;
using Verse;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3847: the catalog's part-loss tables, each damage def's rule from the class its worker's methods are declared on, and the
// production way from the faction defs to a raid's part loss.
public sealed class PartLossCatalogTests
{
    private static readonly DamageArmorCategoryDef Sharp = new() { defName = "Sharp" };

    private static readonly DamageDef Bullet = new() { defName = "Bullet", workerClass = typeof(DamageWorker_AddInjury), defaultDamage = 12, armorCategory = Sharp };

    private static readonly DamageDef Stab = new() { defName = "Stab", workerClass = typeof(DamageWorker_Stab), stabChanceOfForcedInternal = 0.6f, armorCategory = Sharp };

    private static readonly DamageDef Cut = new()
    {
        defName = "Cut", workerClass = typeof(DamageWorker_Cut), cutCleaveBonus = 1.4f, armorCategory = Sharp,
        cutExtraTargetsCurve = new SimpleCurve { new Verse.CurvePoint(0, 0), new Verse.CurvePoint(0.6f, 0), new Verse.CurvePoint(0.9f, 1), new Verse.CurvePoint(1, 2) },
    };

    private static readonly DamageDef Blunt = new()
    {
        defName = "Blunt", workerClass = typeof(DamageWorker_Blunt), bluntInnerHitChance = 0.4f,
        bluntInnerHitDamageFractionToConvert = new FloatRange(0.1f, 0.2f), overkillPctToDestroyPart = new FloatRange(0.4f, 1f),
    };

    private static readonly DamageDef Extinguish = new() { defName = "Extinguish" };

    private static readonly BodyPartDef Arm = new() { defName = "Arm", hitChanceFactors = new Dictionary<DamageDef, float> { [Bullet] = 0.5f } };
    private static readonly BodyPartDef Mind = new() { defName = "Mind", conceptual = true };

    private static readonly RecipeDef InstallArm = new()
    {
        defName = "InstallNaturalArm", workerClass = typeof(Recipe_InstallNaturalBodyPart), appliedOnFixedBodyParts = [Arm],
    };

    private static class Members
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "verbs")]
        internal static extern ref List<VerbProperties>? Verbs(ThingDef def);
    }

    private static readonly ThingDef Projectile = new() { defName = "Bullet_Rifle", projectile = new ProjectileProperties { damageDef = Bullet } };

    private static ThingDef Rifle()
    {
        var gun = new ThingDef
        {
            defName = "Rifle", weaponTags = ["Gun"], generateCommonality = 1,
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

    private static readonly FactionDef Pirates = new()
    {
        defName = "Pirate",
        pawnGroupMakers =
        [
            new PawnGroupMaker
            {
                kindDef = new PawnGroupKindDef { defName = "Combat" }, commonality = 100,
                options = [new PawnGenOption { kind = new PawnKindDef { defName = "Gunner", combatPower = 50, race = Human, weaponTags = ["Gun"] }, selectionWeight = 1 }],
            },
        ],
    };

    private static DefinitionCatalog Catalog() =>
        new([new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, Human, Projectile, Rifle(), Pirates,
                Bullet, Stab, Cut, Blunt, Extinguish, Arm, Mind, InstallArm],
            DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));

    [Fact]
    public void EachDamageDefsRuleFollowsTheClassThatDeclaresItsWorkersMethods()
    {
        var rules = Catalog().PartHitRules();

        // DamageWorker_AddInjury: any depth for a ranged hit, the whole damage preserved, the default 0 to 0.7 overkill, the arm's factor.
        var bullet = rules["Bullet"];
        Assert.Equal((PartHitDraw.HitDepth, 0.0, 0.7, 0.0, false), (bullet.Draw, bullet.OverkillMin, bullet.OverkillMax, bullet.ForcedInternal, bullet.StabSplit));
        Assert.Equal([new PartHitCase(1, 1, true)], bullet.Cases);
        Assert.Equal([new PartHitFactor("Arm", 0.5), new PartHitFactor("Mind", 0)], bullet.HitFactors);

        var stab = rules["Stab"];
        Assert.Equal((PartHitDraw.HitDepth, 0.6, true), (stab.Draw, stab.ForcedInternal, stab.StabSplit));
        Assert.Equal([new PartHitFactor("Mind", 0)], stab.HitFactors);

        // Cut: no extra target below 0.6, one from 0.6 to 0.9 (round-random of the ramp), two at the top; a split one is not preserved.
        var cut = rules["Cut"];
        Assert.Equal(PartHitDraw.Outside, cut.Draw);
        Assert.Equal(1, cut.Cases.Sum(c => c.Chance), 6);
        Assert.Equal(new PartHitCase(cut.Cases[0].Chance, 1, true), cut.Cases[0]);
        Assert.Equal(0.6 + (0.3 / 2), cut.Cases[0].Chance, 3);
        Assert.Equal((1 + 1.4) / (1 + 1 + 1.4), cut.Cases[1].Factor, 6);
        Assert.False(cut.Cases[1].Preserve);

        // Blunt: 0.6 of the hits keep the whole damage, 0.4 convert 0.1 to 0.2 of it inward; never preserved.
        var blunt = rules["Blunt"];
        Assert.Equal((PartHitDraw.Outside, 0.4, 1.0), (blunt.Draw, blunt.OverkillMin, blunt.OverkillMax));
        Assert.Equal(0.6, blunt.Cases[0].Chance, 6);
        Assert.Equal(1 - 0.15, blunt.Cases.Skip(1).Sum(c => c.Chance * c.Factor) / 0.4, 6);
        Assert.All(blunt.Cases, c => Assert.False(c.Preserve));

        Assert.Equal(PartHitDraw.NoInjury, rules["Extinguish"].Draw);
        Assert.Equal(1, Catalog().PartReplacementRecipes()["Arm"]["InstallNaturalArm"]);
    }

    [Fact]
    public void AFreshColonysPirateRaidDealsBulletsTheCatalogRulesAndTakesAnArmWithoutThrowing()
    {
        // The production way (#3822): the faction defs' rifle gives the raid bin its Bullet damage def, the catalog's rule for it and
        // the arm's tier price the loss on a colonist as the native read fills it (a natural arm on the map, one doctor).
        var catalog = Catalog();
        var inputs = HarmInputs.Of(new RoundsFacts
        {
            RaidPoints = Known(200d), HostileFactions = Known<EquatableArray<string>>(["Pirate"]),
            HarmCatalog = new() { Raid = catalog.RaidFactions(), Parts = new() { Rules = catalog.PartHitRules(), Recipes = catalog.PartReplacementRecipes() } },
        });
        var bin = Assert.Single(inputs.Composition.Bins);
        Assert.True(bin.Ranged);
        Assert.Equal([new WeaponDamage("Bullet", 1)], bin.Damages);

        var pawn = ForecastValueTests.HarmPawn() with
        {
            PartLoss = new()
            {
                InstantKillChance = 0.5,
                Parts =
                [
                    new("Torso", 0.7) { Groups = ["Torso"], Outside = true, Core = true, Parent = -1, Health = 40, MaxHealth = 40, Destroyable = true },
                    new("Arm", 0.3) { Groups = ["Arms"], Outside = true, Parent = 0, Health = 4, MaxHealth = 4, Destroyable = true, Manipulation = 0.5 },
                ],
                Replacements = [new("Arm", "InstallNaturalArm") { IngredientsOnMap = true, EligibleDoctors = 1, SuccessChance = 0.9, DoctorSuccessChance = 0.9 }],
            },
        };
        var harm = HarmConsequences.Of(new HitRange(5, 3, 20), inputs.Composition.Bins, [new ArmorResponse { ExpectedDamage = bin.Damage }],
            HarmLedger.PartsOf(inputs, []), pawn, ForecastValueTests.HarmTables(), new ForecastSupportConsequences(), new ForecastValuePolicy(), 10 * GameTime.TicksPerDay);

        Assert.True(harm.Known);
        var arm = Assert.Single(harm.Replacements);
        Assert.Equal(("InstallNaturalArm", SurgeryWantReason.None), (arm.Recipe!.Recipe, arm.Reason));
        Assert.True(arm.Probability > 0);
    }
}
