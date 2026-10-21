using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The analytic hunt estimate (#3533): a roster's chance of harm and death to its gunners, of downing its prey, the
/// nutrition it brings and how long the encounter lasts, from the game's own combat formulas over the observed hunt
/// read. Pure and synchronous: no simulation, no worker. The RealCombat simulator is its offline check
/// (docs/developers/testing/hunting-prediction-qualification.md).
/// <para>
/// The model. The gunners take the roster's prey one at a time, nearest first. Each gunner walks into
/// <see cref="EngageRangeFactor"/> of his range (the hunt job's cast position) and fires casts of warmup, burst and
/// cooldown at ShotReport's hit chance; each hit does the projectile's damage after ApplyArmor's deflect and halve
/// odds. A prey goes down at the hits whose damage reaches pain shock or the lethal damage threshold, so the hits are a
/// Poisson process and its downing a Poisson tail. A gunner-retriever stops firing at the roster's departure tick.
/// A prey that can fight back (<see cref="FightsBack"/>) turns manhunter on each hit with the hunter's revenge chance;
/// it then closes on the nearest gunner and attacks him until it goes down (its packmates join at the pack chance).
/// Its landed, undeflected melee hits are the gunner's harm; reaching the lethal threshold before pain shock is death.
/// </para>
/// <para>
/// Not modelled: cover and darkness, posture, fleeing prey, the hit chance rising as a manhunter closes, vital-part
/// and bleeding deaths, and a gunner downed mid-fight leaving the shooting.
/// </para>
/// </summary>
public static class HuntEstimator
{
    /// <summary>The encounter horizon, 2.4 game hours: the window the qualification protocol measures.</summary>
    public static readonly int Horizon = (int)(GameTime.TicksPerHour * 2.4);
    /// <summary>JobDriver_Hunt's cast position stands within this share of the weapon's range.</summary>
    public const double EngageRangeFactor = 0.95;
    /// <summary>ShotReport's floor on the shooter's factor and on the aim before target size.</summary>
    public const double MinAim = 0.0201;
    /// <summary>The lethal damage threshold is this many injury points per unit of health scale.</summary>
    public const double LethalPerHealthScale = 150;
    private const int Step = 10;
    /// <summary>Hits beyond this many to down are a prey the roster cannot down at all.</summary>
    private const int MaxHits = 1024;

    /// <summary>The roster's estimate, or why it cannot be stated: a missing read is a refusal, never a default.</summary>
    public static (HuntEstimate? Estimate, string Refusal) Estimate(HuntActivityRead read, HuntRoster roster)
    {
        var prey = roster.Prey.Select(id => read.Prey.FirstOrDefault(p => p.Pawn == id)).ToArray();
        if (prey.Length == 0 || prey.Any(p => p is null)) return (null, "prey_unobserved");
        var gunners = roster.Gunners.Select(id => read.Hunters.FirstOrDefault(h => h.Pawn == id)).ToArray();
        if (gunners.Length == 0 || gunners.Any(g => g is null)) return (null, "hunter_unobserved");
        foreach (var g in gunners)
        {
            if (g!.Gun is null || !g.WeaponRange.IsKnown) return (null, "gunner_unarmed:" + g.Pawn);
            if (g.Body is null) return (null, "body_unobserved:" + g.Pawn);
        }
        foreach (var p in prey)
        {
            if (!p!.Meat.IsKnown || !p.MeatNutrition.IsKnown) return (null, "prey_yield_unknown");
            if (p.Body is null) return (null, "body_unobserved:" + p.Pawn);
            if (p.Melee is { Damage: > 0 } && gunners.Any(g => p.Revenge.All(r => r.Hunter != g!.Pawn))) return (null, "revenge_unobserved:" + p.Pawn);
        }
        if (!read.WeatherAccuracy.TryGetValue(out var weather) && gunners.Any(g => !g!.Gun!.IgnoresWeather)) return (null, "weather_unobserved");
        var g2 = gunners.Select(g => g!).ToArray();
        var ordered = prey.Select(p => p!).OrderBy(p => g2.Min(g => Distance(g.Location, p.Location))).ThenBy(p => p.Pawn, StringComparer.Ordinal).ToArray();
        return (Run(g2, ordered, roster, weather), "");
    }

    /// <summary>Whether the prey can harm a gunner of the roster at all: a damaging melee attack and a revenge chance above zero against one of them.</summary>
    public static bool FightsBack(HuntActorRead prey, IEnumerable<string> gunners) =>
        prey.Melee is { Damage: > 0, HitChance: > 0 } && gunners.Any(g => prey.Revenge.Any(r => r.Hunter == g && r.Chance > 0));

    private static HuntEstimate Run(HuntActorRead[] gunners, HuntActorRead[] prey, HuntRoster roster, double weather)
    {
        var steps = Horizon / Step + 1;
        var leaves = gunners.Select(g => roster.GunnerRetriever && roster.Retriever == g.Pawn ? roster.DepartureTick : int.MaxValue).ToArray();
        var survival = new double[prey.Length][];
        var rates = new double[prey.Length][];
        var firing = new double[prey.Length][];
        var nutrition = 0.0; var downed = 1.0; var end = 0.0;
        // First pass: when each prey goes down. The gunners turn to the next prey at the mean tick the last one went down.
        for (var k = 0; k < prey.Length; k++)
        {
            var p = prey[k];
            rates[k] = new double[gunners.Length];
            var starts = firing[k] = new double[gunners.Length];
            for (var i = 0; i < gunners.Length; i++)
            {
                var g = gunners[i];
                var distance = Distance(g.Location, p.Location);
                var reach = Math.Min(distance, EngageRangeFactor * g.WeaponRange.Value);
                var gun = g.Gun!;
                starts[i] = end + (distance - reach) * g.Body!.TicksPerCell + gun.WarmupTicks;
                rates[k][i] = HitsPerTick(gun, HitChance(gun, reach, weather, p.Body!.BodySize));
            }
            var hits = HitsToDown(p.Body!, gunners.Select(g => g.Gun!).ToArray(), rates[k]);
            survival[k] = new double[steps];
            var mean = 0.0;
            for (var s = 0; s < steps; s++)
            {
                var t = s * Step;
                var expected = 0.0;
                for (var i = 0; i < gunners.Length; i++) expected += rates[k][i] * Math.Max(0, Math.Min(t, leaves[i]) - starts[i]);
                survival[k][s] = t < end ? 1 : PoissonBelow(hits, expected);
                if (t >= end && s < steps - 1) mean += survival[k][s] * Step;
            }
            var down = 1 - survival[k][steps - 1];
            nutrition += down * p.Meat.Value * p.MeatNutrition.Value;
            downed *= down;
            end = Math.Min(Horizon, end + mean);
        }
        var ids = gunners.Select(g => g.Pawn).ToArray();
        if (!prey.Any(p => FightsBack(p, ids))) return new(false, 0, 0, downed, nutrition, end);
        // Second pass: the chance each prey turns on the gunners while it stands, and what its attack does once it does.
        var safe = 1.0; var alive = 1.0;
        var remaining = survival.Select(Remaining).ToArray();
        for (var k = 0; k < prey.Length; k++)
        {
            var p = prey[k];
            if (!FightsBack(p, ids)) continue;
            var target = Nearest(gunners, p, out var reach);
            var attack = Attack(p.Melee!, target.Body!);
            var closing = (int)Math.Ceiling(reach * p.Body!.TicksPerCell / Step);
            var pack = prey.Select((m, j) => (m, j)).Where(x => x.j != k && x.m.Herd.Length > 0 && x.m.Herd == p.Herd && x.m.Melee is { Damage: > 0 }).ToArray();
            var chances = gunners.Select(g => p.Revenge.First(r => r.Hunter == g.Pawn)).ToArray();
            double harm = 0, death = 0, unprovoked = 1;
            for (var s = 0; s < steps - 1 && survival[k][s] > 0; s++)
            {
                var t = s * Step;
                double hazard = 0, joined = 0;
                for (var i = 0; i < gunners.Length; i++)
                {
                    if (t < firing[k][i] || t >= leaves[i]) continue;
                    var h = rates[k][i] * chances[i].Chance;
                    hazard += h;
                    joined += h * chances[i].PackChance;
                }
                if (hazard <= 0) continue;
                // Each hit while it stands rolls the hunter's revenge chance; it turns at most once.
                var revenge = unprovoked * survival[k][s] * (1 - Math.Exp(-hazard * Step));
                unprovoked -= revenge;
                var q = joined / hazard;
                var own = attack.Rate * Contact(remaining[k], survival[k][s], s + closing);
                var herd = pack.Sum(x => Attack(x.m.Melee!, target.Body!).Rate * Contact(remaining[x.j], 1, s + (int)Math.Ceiling(reach * x.m.Body!.TicksPerCell / Step)));
                harm += revenge * (q * (1 - Math.Exp(-(own + herd))) + (1 - q) * (1 - Math.Exp(-own)));
                death += revenge * (q * attack.Death(own + herd) + (1 - q) * attack.Death(own));
            }
            safe *= 1 - harm;
            alive *= 1 - death;
        }
        return new(true, 1 - safe, 1 - alive, downed, nutrition, end);
    }

    /// <summary>
    /// ShotReport.HitReportFor at <paramref name="distance"/> against a standing pawn in the open: the shooter's factor (his
    /// per-cell accuracy to the distance times his range-band factor, floored, or 1 for a shot that cannot go wild), the
    /// weapon's banded factor and the weather, floored, then the target's size clamped to 0.5–2.
    /// </summary>
    public static double HitChance(HuntGunRead gun, double distance, double weather, double bodySize)
    {
        var shooter = gun.GoesWild ? Math.Max(Math.Pow(gun.Accuracy, distance) * Band(distance, gun.FactorTouch, gun.FactorShort, gun.FactorMedium, gun.FactorLong), MinAim) : 1;
        var weapon = Math.Clamp(Band(distance, gun.WeaponTouch, gun.WeaponShort, gun.WeaponMedium, gun.WeaponLong), 0.01, 1);
        var aim = Math.Max(shooter * weapon * (gun.IgnoresWeather ? 1 : weather), MinAim);
        return Math.Clamp(aim * Math.Clamp(bodySize, 0.5, 2), 0, 1);
    }

    /// <summary>The game's range bands: touch to 3 cells, then linear to short at 12, medium at 25 and long at 40, long beyond.</summary>
    public static double Band(double distance, double touch, double near, double medium, double far) => distance switch
    {
        <= 3 => touch,
        <= 12 => Lerp(touch, near, (distance - 3) / 9),
        <= 25 => Lerp(near, medium, (distance - 12) / 13),
        <= 40 => Lerp(medium, far, (distance - 25) / 15),
        _ => far,
    };

    /// <summary>Hits per tick of one gunner firing continuously: a cast is warmup, the burst and cooldown, and each shot hits at <paramref name="hitChance"/>.</summary>
    public static double HitsPerTick(HuntGunRead gun, double hitChance) =>
        gun.Burst * hitChance / Math.Max(1, gun.WarmupTicks + gun.CooldownTicks + (gun.Burst - 1) * gun.BurstGapTicks);

    /// <summary>
    /// ApplyArmor's odds for one hit: with a = max(armour − penetration, 0), the hit is deflected below a/2 and halved
    /// below a. Returns the deflect chance and the expected damage of a hit that is not deflected.
    /// </summary>
    public static (double Deflect, double Damage) Armor(double damage, double penetration, double armor)
    {
        var a = Math.Max(armor - penetration, 0);
        var deflect = Math.Min(a / 2, 1);
        var halved = Math.Min(a, 1) - deflect;
        if (deflect >= 1) return (1, 0);
        return (deflect, (damage * (1 - deflect - halved) + damage / 2 * halved) / (1 - deflect));
    }

    /// <summary>
    /// The injury that downs or kills the body: its pain reaching the pain shock threshold (an injury's pain is its
    /// severity times its pain per severity over the health scale), or its injuries reaching the lethal damage threshold.
    /// </summary>
    public static double DamageToDown(HuntBodyRead body, double painPerDamage)
    {
        var lethal = Math.Max(0, LethalPerHealthScale * body.HealthScale - body.Injury);
        if (body.IgnoresPainShock || painPerDamage <= 0) return lethal;
        return Math.Min(lethal, Math.Max(0, body.PainShock - body.Pain) * body.HealthScale / painPerDamage);
    }

    /// <summary>P(N &lt; k) for N Poisson with mean <paramref name="mean"/>: the chance fewer than k hits landed.</summary>
    public static double PoissonBelow(int k, double mean)
    {
        if (k <= 0) return 0;
        if (k > MaxHits || mean <= 0) return 1;
        double term = Math.Exp(-mean), sum = 0;
        if (term == 0) return mean > k ? 0 : 1;
        for (var i = 0; i < k; i++)
        {
            sum += term;
            term *= mean / (i + 1);
        }
        return Math.Clamp(sum, 0, 1);
    }

    private static int HitsToDown(HuntBodyRead body, HuntGunRead[] guns, double[] rates)
    {
        // The gunners' hits pooled: each hit's expected damage is the rate-weighted mean of theirs after armour.
        double total = rates.Sum(), damage = 0, pain = 0;
        if (total <= 0) return int.MaxValue;
        for (var i = 0; i < guns.Length; i++)
        {
            var (deflect, landed) = Armor(guns[i].Damage, guns[i].ArmorPenetration, guns[i].Sharp ? body.ArmorSharp : body.ArmorBlunt);
            damage += rates[i] / total * (1 - deflect) * landed;
            pain += rates[i] / total * guns[i].PainPerDamage;
        }
        return damage <= 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, Math.Max(1, Math.Ceiling(DamageToDown(body, pain) / damage)));
    }

    /// <summary>A manhunter's melee on its target: landed, undeflected hits per tick, and the chance a given count of them kills before pain shock downs him.</summary>
    private sealed record MeleeAttack(double Rate, int Down, int Kill)
    {
        public double Death(double expected) => Kill > Down ? 0 : 1 - PoissonBelow(Kill, expected);
    }

    private static MeleeAttack Attack(HuntMeleeRead melee, HuntBodyRead target)
    {
        var armor = melee.SharpShare * target.ArmorSharp + (1 - melee.SharpShare) * target.ArmorBlunt;
        var (deflect, damage) = Armor(melee.Damage, melee.ArmorPenetration, armor);
        var rate = melee.HitChance * (1 - target.Dodge) * (1 - deflect) / Math.Max(1, melee.CooldownTicks);
        if (damage <= 0) return new(0, int.MaxValue, int.MaxValue);
        var down = (int)Math.Min(int.MaxValue, Math.Ceiling(DamageToDown(target, melee.PainPerDamage) / damage));
        var kill = (int)Math.Min(int.MaxValue, Math.Ceiling(Math.Max(0, LethalPerHealthScale * target.HealthScale - target.Injury) / damage));
        return new(rate * Step, Math.Max(1, down), Math.Max(1, kill));
    }

    /// <summary>Expected steps the prey still stands from <paramref name="from"/> on, given it stood at <paramref name="standing"/>.</summary>
    private static double Contact(double[] remaining, double standing, int from) =>
        from >= remaining.Length || standing <= 0 ? 0 : remaining[from] / standing;

    /// <summary>Suffix sums of a survival curve: the expected steps standing from each step to the horizon.</summary>
    private static double[] Remaining(double[] survival)
    {
        var sums = new double[survival.Length];
        for (var s = survival.Length - 2; s >= 0; s--) sums[s] = sums[s + 1] + survival[s];
        return sums;
    }

    private static HuntActorRead Nearest(HuntActorRead[] gunners, HuntActorRead prey, out double reach)
    {
        var best = gunners.OrderBy(g => Math.Min(Distance(g.Location, prey.Location), EngageRangeFactor * g.WeaponRange.Value)).ThenBy(g => g.Pawn, StringComparer.Ordinal).First();
        reach = Math.Min(Distance(best.Location, prey.Location), EngageRangeFactor * best.WeaponRange.Value);
        return best;
    }

    private static double Distance(Cell a, Cell b) => Math.Sqrt(((double)a.X - b.X) * (a.X - b.X) + ((double)a.Z - b.Z) * (a.Z - b.Z));

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
