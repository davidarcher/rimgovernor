using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HuntEstimatorTests
{
    // A bolt-action rifle in a shooting-8 colonist's hands, roughly as the game states it.
    static readonly HuntGunRead Rifle = new(37, 0.97, 1, 1, 1, 1, 0.65, 0.85, 0.9, 0.8, true, false, 102, 90, 1, 0, 18, 0.27, true, 0.0125);
    static readonly HuntBodyRead Colonist = new(1, 1, 0.8, false, 0, 0, 0, 0, 13, 0);
    static readonly HuntBodyRead BoarBody = new(0.85, 0.85, 0.8, false, 0, 0, 0, 0, 13, 0);
    static readonly HuntMeleeRead Tusk = new(9, 120, 0.6, 0.13, 1, 0.0125);

    static HuntActorRead Hunter(string id, int x = 0) => HuntFixtures.Actor(id, "Colonist", new(x, 0), 30) with
    {
        Weapon = new("Gun_BoltActionRifle", "", "Normal"), Gun = Rifle, Body = Colonist, Available = true,
    };
    static HuntActorRead Boar(string id = "boar", double revenge = 0.5, double pack = 0, int z = 30) => HuntFixtures.Actor(id, "Boar", new(0, z), 3) with
    {
        Body = BoarBody, Melee = Tusk, Revenge = [new("a", revenge, pack), new("b", revenge, pack)],
        Meat = 70, MeatNutrition = 0.05,
    };
    static HuntActorRead Hare() => HuntFixtures.Actor("hare", "Hare", new(0, 30), 1) with
    {
        Body = new(0.2, 0.2, 0.8, false, 0, 0, 0, 0, 10, 0), Melee = new(2, 100, 0.5, 0, 0, 0.0125), Revenge = [new("a", 0, 0), new("b", 0, 0)],
        Meat = 15, MeatNutrition = 0.05,
    };
    static HuntActivityRead Read(params HuntActorRead[] prey) => HuntFixtures.NoHunt with
    {
        Hunters = [Hunter("a"), Hunter("b", 2)], Prey = EquatableArray.From(prey), WeatherAccuracy = 1.0,
    };
    static HuntRoster Roster(string[] gunners, params string[] prey) => new(EquatableArray.From(gunners), EquatableArray.From(prey));
    static HuntEstimate Estimated(HuntActivityRead read, HuntRoster roster)
    {
        var (estimate, refusal) = HuntEstimator.Estimate(read, roster);
        Assert.Equal("", refusal);
        return estimate!;
    }

    [Fact]
    public void TheHitChanceIsTheShootersBandedAccuracyTimesTheWeaponTimesTheWeatherTimesTheTargetSize()
    {
        // Touch: 0.97^3 for the shooter, the weapon's 0.65, clear weather, a standard body.
        Assert.Equal(Math.Pow(0.97, 3) * 0.65, HuntEstimator.HitChance(Rifle, 3, 1, 1), 9);
        // Rain halves it; a small target counts as 0.5 and a huge one as 2.
        Assert.Equal(Math.Pow(0.97, 20) * HuntEstimator.Band(20, 0.65, 0.85, 0.9, 0.8) * 0.5, HuntEstimator.HitChance(Rifle, 20, 0.5, 1), 9);
        Assert.Equal(HuntEstimator.HitChance(Rifle, 20, 1, 0.5), HuntEstimator.HitChance(Rifle, 20, 1, 0.1), 9);
        Assert.Equal(Math.Min(1, 2 * HuntEstimator.HitChance(Rifle, 20, 1, 1)), HuntEstimator.HitChance(Rifle, 20, 1, 5), 9);
        // A shot that cannot go wild ignores the shooter; a hopeless shot is floored before the size.
        Assert.Equal(0.65, HuntEstimator.HitChance(Rifle with { GoesWild = false }, 3, 1, 1), 9);
        Assert.Equal(HuntEstimator.MinAim * 0.5, HuntEstimator.HitChance(Rifle with { Accuracy = 0.5 }, 40, 1, 0.2), 9);
        // A weapon that ignores accuracy maluses ignores the weather.
        Assert.Equal(HuntEstimator.HitChance(Rifle, 20, 1, 1), HuntEstimator.HitChance(Rifle with { IgnoresWeather = true }, 20, 0.5, 1), 9);
    }

    [Fact]
    public void TheRangeBandsInterpolateBetweenTheirEdges()
    {
        Assert.Equal(1, HuntEstimator.Band(2, 1, 2, 3, 4));
        Assert.Equal(1.5, HuntEstimator.Band(7.5, 1, 2, 3, 4), 9);
        Assert.Equal(3, HuntEstimator.Band(25, 1, 2, 3, 4), 9);
        Assert.Equal(4, HuntEstimator.Band(60, 1, 2, 3, 4));
    }

    [Fact]
    public void ACastIsWarmupBurstAndCooldown()
    {
        var burst = Rifle with { Burst = 3, BurstGapTicks = 10, WarmupTicks = 60, CooldownTicks = 60 };
        Assert.Equal(3 * 0.5 / 140, HuntEstimator.HitsPerTick(burst, 0.5), 12);
        Assert.Equal(0.5 / 192, HuntEstimator.HitsPerTick(Rifle, 0.5), 12);
    }

    [Fact]
    public void ArmorDeflectsBelowHalfItsMarginAndHalvesBelowIt()
    {
        // a = 0.4: 20% deflected, 20% halved, 60% whole; a hit that lands does (0.6*10 + 0.2*5) / 0.8.
        var (deflect, damage) = HuntEstimator.Armor(10, 0.1, 0.5);
        Assert.Equal(0.2, deflect, 9);
        Assert.Equal(8.75, damage, 9);
        Assert.Equal((0.0, 10.0), HuntEstimator.Armor(10, 0.5, 0.3));
        Assert.Equal((1.0, 0.0), HuntEstimator.Armor(10, 0, 2.5));
    }

    [Fact]
    public void ABodyGoesDownAtPainShockOrTheLethalThresholdWhicheverComesFirst()
    {
        // Pain 0.0125 per point on a health scale of 1: 64 points reach the 0.8 threshold, before the 150 lethal.
        Assert.Equal(64, HuntEstimator.DamageToDown(Colonist, 0.0125), 9);
        // Already in pain, less is needed; a body that ignores pain shock goes down only at the lethal threshold, less what it already took.
        Assert.Equal(32, HuntEstimator.DamageToDown(Colonist with { Pain = 0.4 }, 0.0125), 9);
        Assert.Equal(150, HuntEstimator.DamageToDown(Colonist with { IgnoresPainShock = true }, 0.0125), 9);
        Assert.Equal(20, HuntEstimator.DamageToDown(Colonist with { Injury = 130 }, 0.0125), 9);
        // A smaller health scale scales both.
        Assert.Equal(0.85 * 64, HuntEstimator.DamageToDown(BoarBody, 0.0125), 9);
    }

    [Fact]
    public void ThePoissonTailIsTheChanceTooFewHitsLanded()
    {
        Assert.Equal(Math.Exp(-2), HuntEstimator.PoissonBelow(1, 2), 12);
        Assert.Equal(Math.Exp(-2) * 3, HuntEstimator.PoissonBelow(2, 2), 12);
        Assert.Equal(0, HuntEstimator.PoissonBelow(0, 2));
        Assert.Equal(1, HuntEstimator.PoissonBelow(3, 0));
        Assert.True(HuntEstimator.PoissonBelow(5, 1000) < 1e-9);
    }

    [Fact]
    public void AHareIsNeverRiskEstimated()
    {
        var estimate = Estimated(Read(Hare()), Roster(["a"], "hare"));
        Assert.False(estimate.RiskEstimated);
        Assert.Equal((0.0, 0.0), (estimate.HarmChance, estimate.DeathChance));
        Assert.True(estimate.DownedChance > 0.99);
        Assert.Equal(estimate.DownedChance * 15 * 0.05, estimate.ExpectedNutrition, 9);
        Assert.True(estimate.EncounterTicks > 0);
        Assert.False(HuntEstimator.FightsBack(Hare(), ["a"]));
        // An animal without a melee attack cannot fight back whatever its revenge chance.
        Assert.False(HuntEstimator.FightsBack(Boar() with { Melee = null }, ["a"]));
    }

    [Fact]
    public void ABoarsRevengeChanceReachesTheEstimate()
    {
        var calm = Estimated(Read(Boar(revenge: 0)), Roster(["a"], "boar"));
        Assert.False(calm.RiskEstimated);
        var touchy = Estimated(Read(Boar(revenge: 0.1)), Roster(["a"], "boar"));
        var angry = Estimated(Read(Boar(revenge: 0.6)), Roster(["a"], "boar"));
        Assert.True(touchy.RiskEstimated && angry.RiskEstimated);
        Assert.InRange(touchy.HarmChance, 1e-6, 1);
        Assert.True(angry.HarmChance > touchy.HarmChance);
        Assert.True(angry.DeathChance >= touchy.DeathChance);
        // The revenge never changes how fast the boar goes down.
        Assert.Equal(calm.DownedChance, angry.DownedChance, 9);
    }

    [Fact]
    public void ASecondGunnerDownsThePreySoonerAndAGunnerRetrieverLeavingStopsHisShots()
    {
        var read = Read(Boar());
        var solo = Estimated(read, Roster(["a"], "boar"));
        var pair = Estimated(read, Roster(["a", "b"], "boar"));
        Assert.True(pair.EncounterTicks < solo.EncounterTicks);
        Assert.True(pair.HarmChance < solo.HarmChance);
        // b leaves at once: the pair is a solo for downing, while b's shots before leaving are no revenge roll either.
        var leftAtOnce = Estimated(read, Roster(["a", "b"], "boar") with { Retrievers = ["b"], DepartureTick = 1 });
        Assert.Equal(solo.EncounterTicks, leftAtOnce.EncounterTicks, 0);
        var leftLate = Estimated(read, Roster(["a", "b"], "boar") with { Retrievers = ["b"], DepartureTick = HuntEstimator.Horizon });
        Assert.Equal(pair.EncounterTicks, leftLate.EncounterTicks, 6);
        Assert.Equal(pair.HarmChance, leftLate.HarmChance, 9);
    }

    [Fact]
    public void ArmourAndAHerdMateChangeTheHarm()
    {
        var bare = Estimated(Read(Boar()), Roster(["a"], "boar"));
        var armoured = Estimated(Read(Boar()) with { Hunters = [Hunter("a") with { Body = Colonist with { ArmorSharp = 1.2 } }, Hunter("b", 2)] }, Roster(["a"], "boar"));
        Assert.True(armoured.HarmChance < bare.HarmChance);
        var herd = Read(Boar() with { Herd = "h" }, Boar("boar2", pack: 1, z: 32) with { Herd = "h" });
        var both = Estimated(herd, Roster(["a"], "boar", "boar2"));
        var one = Estimated(Read(Boar()), Roster(["a"], "boar"));
        Assert.True(both.HarmChance > one.HarmChance);
        Assert.True(both.DownedChance <= one.DownedChance);
        Assert.True(both.ExpectedNutrition > one.ExpectedNutrition);
    }

    [Fact]
    public void AMissingReadIsARefusalNeverADefault()
    {
        var roster = Roster(["a"], "boar");
        string Refusal(HuntActivityRead read, HuntRoster? r = null) => HuntEstimator.Estimate(read, r ?? roster).Refusal;
        Assert.Equal("prey_unobserved", Refusal(Read()));
        Assert.Equal("hunter_unobserved", Refusal(Read(Boar()), Roster(["x"], "boar")));
        Assert.Equal("gunner_unarmed:a", Refusal(Read(Boar()) with { Hunters = [Hunter("a") with { Gun = null }] }));
        Assert.Equal("body_unobserved:a", Refusal(Read(Boar()) with { Hunters = [Hunter("a") with { Body = null }] }));
        Assert.Equal("body_unobserved:boar", Refusal(Read(Boar() with { Body = null })));
        Assert.Equal("revenge_unobserved:boar", Refusal(Read(Boar() with { Revenge = [] })));
    }
}
