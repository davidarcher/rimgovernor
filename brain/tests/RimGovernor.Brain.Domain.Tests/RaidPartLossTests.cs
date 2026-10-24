using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3847: the parts one raid destroys on one colonist, from the game's damage rules over the pawn's own part reads, and the
/// replacement surgeries those losses book in the hospital credit.
/// </summary>
public sealed class RaidPartLossTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Days = 10;
    private static readonly HitRange Hits = new(5, 3, 20);

    // Vanilla Bullet: DamageWorker_AddInjury, the whole damage on the hit part, ReduceDamageToPreserveOutsideParts, overkill 0 to 0.7.
    internal static readonly PartHitRule Bullet = new() { Draw = PartHitDraw.HitDepth, Cases = [new(1, 1, true)], OverkillMin = 0, OverkillMax = 0.7 };

    // A DamageWorker_Stab def: half its Outside hits forced inside, 0.4 of the damage on a part stabbed through.
    private static readonly PartHitRule Stab = Bullet with { ForcedInternal = 0.5, StabSplit = true };

    internal static readonly RaidPartCatalog Catalog = new()
    {
        Rules = new Dictionary<string, PartHitRule>
        {
            ["Bullet"] = Bullet, ["Stab"] = Stab, ["Blunt"] = Bullet with { Draw = PartHitDraw.Outside, Cases = [new(1, 1, false)] },
            ["Flame"] = new() { Draw = PartHitDraw.NoInjury }, ["Modded"] = new() { Draw = PartHitDraw.ForeignWorker },
        },
        Recipes = new Dictionary<string, IReadOnlyDictionary<string, double>>
        {
            ["Arm"] = new Dictionary<string, double> { ["InstallNaturalArm"] = 1, ["InstallBionicArm"] = 3 },
        },
    };

    /// <summary>A raid bin of <paramref name="damage"/> sharp per hit dealing <paramref name="def"/>.</summary>
    internal static WeaponBin Gun(double damage, string def = "Bullet", bool ranged = true) =>
        new() { Damage = damage, Category = ArmorCategory.Sharp, Weight = 1, Ranged = ranged, Damages = [new(def, 1)] };

    // A torso (core, 0.6), an arm on it (Outside, 0.3, health 10) and a heart in it (Inside, 0.1, health 5, its loss kills).
    private static readonly BodyPartLossRead Torso = new("Torso", 0.6)
    {
        Groups = ["Torso"], Outside = true, Core = true, Parent = -1, Health = 40, MaxHealth = 40, Destroyable = true,
        Consciousness = 1, Moving = 1, Manipulation = 1, Sight = 1, Talking = 1,
    };

    private static readonly BodyPartLossRead Arm = new("Arm", 0.3)
    {
        Groups = ["Arms"], Outside = true, Parent = 0, Health = 10, MaxHealth = 10, Destroyable = true,
        Consciousness = 1, Moving = 1, Manipulation = 0.5, Sight = 1, Talking = 1,
    };

    private static readonly BodyPartLossRead Heart = new("Heart", 0.1)
    {
        Groups = ["Torso"], Parent = 0, Health = 5, MaxHealth = 5, Destroyable = true, Kills = true,
    };

    private static OrganReplacementRead Replacement(string recipe, bool stocked = true, int doctors = 1) => new("Arm", recipe)
    {
        PartThing = recipe.Replace("Install", ""), PartOnMap = stocked, MedicineOnMap = stocked, EligibleDoctors = doctors, SuccessChance = 0.9, DoctorSuccessChance = 0.9,
        Outcomes = [new("success", 0, 0, 0), new("death", 0.5, 0, 0)], Environment = true, QualityMax = 0.98, Quality = 0.9,
    };

    /// <summary>A torso and a 5-health arm whose natural replacement is stocked (#3847's hospital rate): <paramref name="environment"/> false leaves the bill out of the credit.</summary>
    internal static PawnPartLossFacts ArmLoss(bool environment = true) => new()
    {
        InstantKillChance = 1, Parts = [Torso, Arm with { Health = 5, MaxHealth = 5 }],
        Replacements = [Replacement("InstallNaturalArm") with { Environment = environment }],
    };

    private static NeedsForecastPawn Pawn(double instantKill = 1, params OrganReplacementRead[] replacements) => ForecastValueTests.HarmPawn() with
    {
        PartLoss = new() { InstantKillChance = instantKill, Parts = [Torso, Arm, Heart], Replacements = EquatableArray.From(replacements) },
    };

    private static readonly double[] Whole = [1, 1, 1, 1, 1];

    private static List<PartLossEvent> Of(WeaponBin bin, NeedsForecastPawn? pawn = null, SortedSet<string>? findings = null) =>
        RaidPartLoss.Of(Hits, [bin], new RaidPartInputs { Catalog = Catalog }, pawn ?? Pawn(), Whole, 0.15, 1, Days, findings ?? []);

    // The chance some of a raid's Poisson hits destroys a part one hit destroys with q: the exposure range's three points.
    private static double PerRaid(double q) =>
        (0.185 * (1 - Math.Exp(-3 * q))) + (0.63 * (1 - Math.Exp(-5 * q))) + (0.185 * (1 - Math.Exp(-20 * q)));

    private static PartLossEvent? Lost(List<PartLossEvent> events, string part) => events.SingleOrDefault(e => e.Part == part);

    [Fact]
    public void ARangedHitDestroysEachPartItReachesAtItsHealthByCoverageAndNeverTheCorePart()
    {
        // 20 damage: the arm's 10 over its 10 max health is past the 0.7 overkill top, the heart is inside and not preserved.
        var events = Of(Gun(20), Pawn(1, Replacement("InstallNaturalArm")));

        Assert.Null(Lost(events, "Torso"));
        Assert.Equal(PerRaid(0.3), Lost(events, "Arm")!.Probability, 12);
        Assert.Equal(PerRaid(0.1), Lost(events, "Heart")!.Probability, 12);
        Assert.True(Lost(events, "Heart")!.Kills);
    }

    [Fact]
    public void APreservedOutsidePartFallsOnlyToTheOverkillDrawAndAHitUnderItsHealthDestroysNothing()
    {
        // 13.5 damage: 3.5 over the arm's 10 max health is 0.35, half way through the 0 to 0.7 overkill range.
        Assert.Equal(PerRaid(0.3 * 0.5), Lost(Of(Gun(13.5)), "Arm")!.Probability, 12);
        // A blunt hit is never preserved: at its health the arm goes.
        Assert.Equal(PerRaid(0.3 / 0.9), Lost(Of(Gun(10, "Blunt")), "Arm")!.Probability, 12);
        Assert.Empty(Of(Gun(4)));
    }

    [Fact]
    public void AMeleeHitLandsOnOutsidePartsOnlyAndAStabIsForcedInsideAtTheInnerShare()
    {
        // Melee: the torso and the arm share the draw 0.6 to 0.3, the heart is out of reach.
        var melee = Of(Gun(20, ranged: false));
        Assert.Equal(PerRaid(0.3 / 0.9), Lost(melee, "Arm")!.Probability, 12);
        Assert.Null(Lost(melee, "Heart"));

        // A stab through the torso is forced into the heart half the time with 0.4 of its damage: 8 kills the 5-health heart, 4 does not.
        Assert.Equal(PerRaid(0.6 / 0.9 * 0.5), Lost(Of(Gun(20, "Stab", ranged: false)), "Heart")!.Probability, 12);
        Assert.Null(Lost(Of(Gun(10, "Stab", ranged: false)), "Heart"));
    }

    [Fact]
    public void AStockedReplacementIsBookedAtNoLossAndOtherwiseTheLossLastsTheHorizonWithItsReason()
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        // The bionic arm is the higher tier but unstocked; the natural arm the autopilot would queue.
        var booked = Lost(Of(Gun(20), Pawn(1, Replacement("InstallBionicArm", stocked: false), Replacement("InstallNaturalArm")), findings), "Arm")!;
        Assert.Equal("InstallNaturalArm", booked.Pick!.Recipe);
        Assert.Equal(0, booked.Loss);
        Assert.DoesNotContain(StockArrival.TradeUnmodelled, findings);

        // Halved manipulation to the horizon.
        var expected = Days * (1 - ForecastValue.CapabilityFactor(Whole, [1, 1, 0.5, 1, 1], 0.15));
        var shortOf = Lost(Of(Gun(20), Pawn(1, Replacement("InstallNaturalArm", stocked: false))), "Arm")!;
        Assert.Equal((null, SurgeryWantReason.PartShort), (shortOf.Pick, shortOf.Reason));
        Assert.Equal(expected, shortOf.Loss, 12);
        Assert.Equal(SurgeryWantReason.PartShort, Lost(Of(Gun(20)), "Arm")!.Reason);
        Assert.Equal(SurgeryWantReason.NoDoctor, Lost(Of(Gun(20), Pawn(1, Replacement("InstallNaturalArm", doctors: 0))), "Arm")!.Reason);
    }

    [Fact]
    public void AMissingRuleOrTierThrowsAndAModdedWorkerIsNamed()
    {
        Assert.Equal("part_hit_rule:Ghost", Assert.Throws<RequiredFactException>(() => Of(Gun(20, "Ghost"))).Fact);
        Assert.Equal("weapon_bin_damage", Assert.Throws<RequiredFactException>(() => Of(Gun(20) with { Damages = [] })).Fact);
        var untiered = Replacement("InstallNaturalArm") with { Recipe = "InstallPegArm" };
        Assert.Equal("organ_replacement_tier:InstallPegArm", Assert.Throws<RequiredFactException>(() => Of(Gun(20), Pawn(1, untiered))).Fact);

        var findings = new SortedSet<string>(StringComparer.Ordinal);
        Assert.Empty(Of(Gun(20, "Modded"), findings: findings));
        Assert.Contains("raid_part_hit_unmodelled:Modded", findings);
        Assert.Empty(Of(Gun(20, "Flame")));
    }

    private static HarmDistribution Harm(NeedsForecastPawn pawn, double damage = 20) =>
        HarmConsequences.Of(Hits, [Gun(damage)], [new ArmorResponse { ExpectedDamage = damage, BleedChance = 0 }], new RaidPartInputs { Catalog = Catalog },
            pawn, ForecastValueTests.HarmTables(), new HarmConsequences.Care(true, 0.6, 0, 1), new ForecastValuePolicy(), Days * Day);

    [Fact]
    public void AVitalPartsLossIsDeathUnlessTheInstantKillProtectionKeepsItAndAnArmsLossIsAReplacement()
    {
        var unprotected = Harm(Pawn(1, Replacement("InstallNaturalArm")));
        var protectedPawn = Harm(Pawn(0, Replacement("InstallNaturalArm")));
        var noParts = Harm(ForecastValueTests.HarmPawn());

        Assert.Equal(noParts.DeathProbability, protectedPawn.DeathProbability, 12);
        Assert.True(unprotected.DeathProbability > protectedPawn.DeathProbability);
        Assert.Contains("injury_part_efficiency_unassessed", unprotected.Findings);
        Assert.DoesNotContain("injury_part_effects_unassessed", unprotected.Findings);

        // The arm's loss is booked on the share of the raid that survives the injuries and the heart.
        var arm = Assert.Single(unprotected.Replacements);
        Assert.Equal(("Arm", "InstallNaturalArm"), (arm.Part, arm.Recipe!.Recipe));
        Assert.InRange(arm.Probability, 0.01, PerRaid(0.3));
        // An unstocked arm costs its lost manipulation instead.
        var mean = (HarmDistribution d) => d.Points.Sum(p => p.Loss * p.Probability);
        Assert.True(mean(Harm(Pawn(0))) > mean(protectedPawn));
    }
}
