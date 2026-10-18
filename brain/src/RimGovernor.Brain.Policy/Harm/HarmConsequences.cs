using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>One outcome of a raid for one pawn: the capability-days it loses and the probability of it.</summary>
public readonly record struct HarmPoint(double Loss, double Probability);

/// <summary>
/// What one raid costs one pawn, as a small discrete distribution (#3544): <see cref="Points"/> sum to probability 1,
/// <see cref="DownProbability"/> is the share of the mass that is downed and alive, <see cref="DeathProbability"/> the share
/// that dies (those points carry the remaining horizon plus the terminal value). Unknown (<see cref="Known"/> false, no
/// points) when an input the whole distribution rests on is unobserved. <see cref="Findings"/> name every part left unassessed.
/// </summary>
public sealed record HarmDistribution
{
    public bool Known { get; init; }

    public IReadOnlyList<HarmPoint> Points { get; init; } = [];

    public double DownProbability { get; init; }

    public double DeathProbability { get; init; }

    public IReadOnlyList<string> Findings { get; init; } = [];
}

/// <summary>
/// The health consequences of a raid's hits (harm model, step 5): injury through pain, the course of healing, bleeding until
/// first care, downs, death and wound infection, priced in capability-days from the game's own rules. Pure: the hits are the
/// exposure range (<see cref="RaidExposure"/>), the response the per-bin armor arrays (<see cref="ArmorResponseRules"/>), the
/// support the outcome's care facts. The constants it mirrors are in kept-constants, "Mirrored game rules: raid health
/// consequences (#3544)".
/// </summary>
public static class HarmConsequences
{
    /// <summary>The points of the damage grid: the sum of a raid's hits is spread over this many values.</summary>
    public const int GridPoints = 32;

    // Pearson-Tukey three-point weights for the 5th, 50th and 95th percentiles (Keefer and Bodily 1983): the range's
    // low, mean and high stand for the 90% range the exposure table holds.
    private const double LowWeight = 0.185;
    private const double MeanWeight = 0.63;
    private const double HighWeight = 0.185;

    // Pawn_HealthTracker.ShouldBeDeadFromLethalDamageThreshold: total injury severity at which the pawn dies, per health scale.
    private const double LethalDamagePerHealthScale = 150;

    // Pawn_HealthTracker.HealthTick, every 600 ticks: one injury heals 8 (+4 when not standing) x health scale x 0.01 x
    // InjuryHealingFactor, so 8 (12 lying) severity a day per health scale; a tended and healing injury heals a further
    // 8 x Lerp(0.5, 1.5, tend quality) x health scale x 0.01 x InjuryHealingFactor.
    private const double NaturalHealPerDay = 8;
    private const double LyingHealPerDay = 12;
    private const double TendedHealPerDay = 8;
    private const double TendedHealBase = 0.5;

    // HediffDef Cut/Gunshot/Bite/Blunt injuryProps: painPerSeverity, so Hediff_Injury.PainOffset = severity x 0.0125 / health scale
    // (0.8 pain, the shock threshold, is about 64 severity); injuryProps.bleedRate of the bleeding kinds (Hediff_Injury.BleedRate).
    private const double PainPerSeverity = 0.0125;
    private const double BleedPerSeverity = 0.06;

    // PawnCapacityWorker_Consciousness: LerpDouble(0.1, 1, 0, 0.4, pain) clamped to [0, 0.4] comes off consciousness.
    private const double PainFree = 0.1;
    private const double PainFull = 1;
    private const double PainMaxPenalty = 0.4;

    // Hediff_Injury.AgeTicksToStopBleeding: an untended wound stops bleeding after 90000 + Lerp(0, 90000, InverseLerp(1, 30, severity)) ticks.
    private const double StopBleedingBaseDays = 1.5;
    private const double StopBleedingExtraDays = 1.5;
    private const double StopBleedingSeverityLo = 1;
    private const double StopBleedingSeverityHi = 30;

    // HediffGiver_Bleeding: every 60 ticks, BleedRateTotal >= 0.1 adds BleedRateTotal x 0.001 blood loss (1 a day per unit of
    // rate), otherwise it falls by 0.00033333 (1/3 a day).
    private const double BleedingGiverThreshold = 0.1;
    private const double BloodLossRecoveryPerDay = 1d / 3;

    // HediffCompProperties_Infecter.infectionChance of the injuries that can infect; HediffComp_Infecter.CheckMakeInfection
    // scales it by InfectionChanceFactorFromTendQualityCurve (0 -> 0.7, 1 -> 0.4) and the tending room's InfectionChanceFactor
    // when tended, and by InfectionChanceFactorFromSeverityCurve (1 -> 0.1, 12 -> 1) of the wound's severity.
    private const double InfectionChance = 0.15;
    private const double InfectionTendFactorUntendedQuality = 0.7;
    private const double InfectionTendFactorFullQuality = 0.4;
    private const double InfectionSeverityFactorLo = 0.1;
    private const double InfectionSeverityLo = 1;
    private const double InfectionSeverityHi = 12;

    private const int HealingSteps = 48;

    /// <summary>The consequences of <paramref name="hits"/> raid hits on <paramref name="pawn"/>; see the class summary.</summary>
    /// <param name="hits">Hits the pawn takes in a raid, as the exposure table's 90% range.</param>
    /// <param name="bins">The weapon bins of the raid's composition, weighted by <see cref="WeaponBin.Weight"/>.</param>
    /// <param name="response">The armor response to each of <paramref name="bins"/> (<see cref="ArmorResponseRules.OfOutfit"/>).</param>
    /// <param name="tables">The catalog's penalty tables; blood loss and wound infection are read, an unknown one leaves its part unassessed.</param>
    /// <param name="support">The outcome's care facts; null is unknown, which means untended.</param>
    /// <param name="horizon">Ticks the loss is counted over.</param>
    public static HarmDistribution Of(HitRange hits, IReadOnlyList<WeaponBin> bins, IReadOnlyList<ArmorResponse> response, NeedsForecastPawn pawn,
        CapacityPenaltyTables tables, ForecastSupportConsequences? support, ForecastValuePolicy policy, double horizon)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal)
        {
            "adaptation_feedback_unassessed",
            "rescue_labor_unassessed",
            "injury_part_effects_unassessed",
            "bleed_part_factors_unobserved",
        };
        if (!pawn.PainShockThreshold.TryGetValue(out var shock) || !pawn.HealthScale.TryGetValue(out var healthScale)
            || !pawn.IncomingDamageFactor.TryGetValue(out var incoming) || !pawn.InjuryHealingFactor.TryGetValue(out var healing)
            || ForecastValue.Levels(pawn) is not { } levels || !(healthScale > 0))
        {
            findings.Add("capacities_unobserved:" + pawn.Pawn);
            return Unknown(findings);
        }

        if (tables.MovingMinForCapable is not { } movingMin)
        {
            findings.Add("capacity_penalty_table_unknown:MovingMinForCapable");
            return Unknown(findings);
        }

        var mix = Mix.Of(bins, response);
        if (mix is null || !(horizon > 0))
        {
            findings.Add("weapon_mix_unobserved");
            return Unknown(findings);
        }

        var model = new Model(mix, levels, movingMin, shock, healthScale, Math.Max(0, incoming), Math.Max(0, healing), tables, support, policy, horizon, findings);
        var (mass, step) = Mass(hits, mix);
        var points = new List<HarmPoint>();
        double down = 0, death = 0;
        for (var i = 0; i < mass.Length; i++)
        {
            if (mass[i] > 1e-12)
            {
                model.Evaluate(i * step, mass[i], points, ref down, ref death);
            }
        }

        return new HarmDistribution { Known = true, Points = points, DownProbability = down, DeathProbability = death, Findings = [.. findings] };
    }

    private static HarmDistribution Unknown(SortedSet<string> findings) => new() { Findings = [.. findings] };

    // The sum of the hits' damage on the grid: Poisson hits at the range's low, mean and high, weighted three-point.
    private static (double[] Mass, double Step) Mass(HitRange hits, Mix mix)
    {
        var low = Math.Max(0, hits.Lo);
        var mean = Math.Max(low, hits.Mean);
        var high = Math.Max(mean, hits.Hi);
        var top = high * mix.MeanDamage + 4 * Math.Sqrt(high * mix.SecondMoment);
        var step = top > 0 ? top / (GridPoints - 1) : 1;
        var single = new double[GridPoints];
        for (var b = 0; b < mix.Damage.Length; b++)
        {
            var at = mix.Damage[b] / step;
            var index = Math.Min((int)at, GridPoints - 1);
            var fraction = index == GridPoints - 1 ? 0 : at - index;
            single[index] += mix.Share[b] * (1 - fraction);
            if (fraction > 0)
            {
                single[index + 1] += mix.Share[b] * fraction;
            }
        }

        var mass = new double[GridPoints];
        Add(mass, Compound(low, single), LowWeight);
        Add(mass, Compound(mean, single), MeanWeight);
        Add(mass, Compound(high, single), HighWeight);
        return (mass, step);
    }

    private static void Add(double[] mass, double[] part, double weight)
    {
        for (var i = 0; i < mass.Length; i++)
        {
            mass[i] += part[i] * weight;
        }
    }

    // Poisson(lambda) hits, each drawn from `single`: the distribution of their summed damage, overflow held at the last point.
    private static double[] Compound(double lambda, double[] single)
    {
        lambda = Math.Min(lambda, 500);
        var result = new double[GridPoints];
        var current = new double[GridPoints];
        current[0] = 1;
        var weight = Math.Exp(-lambda);
        var counted = weight;
        result[0] = weight;
        var most = (int)Math.Ceiling(lambda + 8 * Math.Sqrt(lambda) + 10);
        for (var n = 1; n <= most; n++)
        {
            var next = new double[GridPoints];
            for (var i = 0; i < GridPoints; i++)
            {
                if (current[i] == 0)
                {
                    continue;
                }

                for (var j = 0; j < GridPoints; j++)
                {
                    if (single[j] != 0)
                    {
                        next[Math.Min(i + j, GridPoints - 1)] += current[i] * single[j];
                    }
                }
            }

            current = next;
            weight *= lambda / n;
            counted += weight;
            for (var i = 0; i < GridPoints; i++)
            {
                result[i] += weight * current[i];
            }
        }

        // The Poisson tail beyond `most` is held at the worst point.
        result[GridPoints - 1] += Math.Max(0, 1 - counted);
        return result;
    }

    // The weapon mix reduced to what the consequences read.
    private sealed class Mix
    {
        public double[] Share = [];
        public double[] Damage = [];

        /// <summary>Damage a hit does, averaged over the mix (armor applied).</summary>
        public double MeanDamage;

        public double SecondMoment;

        /// <summary>Bleeding severity a hit adds: the chance it is an unhalved sharp hit times the bin's full damage.</summary>
        public double BleedSeverity;

        /// <summary>Wounds a hit makes: one unless some layer deflects it.</summary>
        public double Wounds;

        public static Mix? Of(IReadOnlyList<WeaponBin> bins, IReadOnlyList<ArmorResponse> response)
        {
            if (bins.Count == 0 || bins.Count != response.Count || bins.Any(b => !(b.Weight >= 0)))
            {
                return null;
            }

            var total = bins.Sum(b => b.Weight);
            if (!(total > 0))
            {
                return null;
            }

            var mix = new Mix { Share = new double[bins.Count], Damage = new double[bins.Count] };
            for (var i = 0; i < bins.Count; i++)
            {
                var share = bins[i].Weight / total;
                var damage = Math.Max(0, response[i].ExpectedDamage);
                mix.Share[i] = share;
                mix.Damage[i] = damage;
                mix.MeanDamage += share * damage;
                mix.SecondMoment += share * damage * damage;
                mix.BleedSeverity += share * Math.Clamp(response[i].BleedChance, 0, 1) * bins[i].Damage;
                mix.Wounds += share * (1 - Math.Clamp(response[i].DeflectChance, 0, 1));
            }

            return mix;
        }
    }

    private sealed class Model
    {
        private readonly Mix mix;
        private readonly double[] levels;
        private readonly double movingMin;
        private readonly double shock;
        private readonly double healthScale;
        private readonly double incoming;
        private readonly double healing;
        private readonly CapacityPenaltyTables tables;
        private readonly ForecastValuePolicy policy;
        private readonly double horizon;
        private readonly double days;
        private readonly bool tended;
        private readonly double tendQuality;
        private readonly double careRoomFactor;
        private readonly double firstCareDays;
        private readonly bool bleedingPriced;
        private readonly bool infectionPriced;

        public Model(Mix mix, double[] levels, double movingMin, double shock, double healthScale, double incoming, double healing,
            CapacityPenaltyTables tables, ForecastSupportConsequences? support, ForecastValuePolicy policy, double horizon, SortedSet<string> findings)
        {
            this.mix = mix;
            this.levels = levels;
            this.movingMin = movingMin;
            this.shock = shock;
            this.healthScale = healthScale;
            this.incoming = incoming;
            this.healing = healing;
            this.tables = tables;
            this.policy = policy;
            this.horizon = horizon;
            days = horizon / GameTime.TicksPerDay;
            firstCareDays = double.PositiveInfinity;
            careRoomFactor = 1;

            // Tended only when the outcome serves the care (a service, its tend quality observed); the quality and room factor
            // the support sums over its services are their mean.
            var services = support is null || support.CareServices.Length == 0 ? 0 : support.CareServices.Split(';').Length;
            if (support is not null && services > 0 && support.TendQuality.TryGetValue(out var quality))
            {
                tended = true;
                tendQuality = Math.Clamp(quality / services, 0, 1);
                firstCareDays = Math.Max(0, support.FirstCareTicks) / GameTime.TicksPerDay;
                if (support.CareRoomFactor.TryGetValue(out var room))
                {
                    careRoomFactor = Math.Max(0, room / services);
                }
                else
                {
                    findings.Add("infection_room_factor_unobserved");
                }
            }
            else
            {
                findings.Add("harm_untended");
            }

            bleedingPriced = tables.BloodLoss.Valid();
            if (!bleedingPriced)
            {
                findings.Add("capacity_penalty_table_unknown:" + nameof(tables.BloodLoss));
            }

            infectionPriced = tables.WoundInfection.Valid() && tables.WoundInfection.Immunizable is { ImmunityPerDaySick: > 0 };
            if (infectionPriced)
            {
                findings.Add("infection_immunity_gain_speed_unobserved");
                findings.Add("infection_difficulty_factor_unobserved");
                findings.Add("infection_onset_delay_unmodelled");
            }
            else
            {
                findings.Add("capacity_penalty_table_unknown:" + nameof(tables.WoundInfection));
            }
        }

        // One grid point: the raid dealt `damage` of injury severity before the incoming-damage factor.
        public void Evaluate(double damage, double mass, List<HarmPoint> points, ref double down, ref double death)
        {
            var severity = damage * incoming;
            if (severity <= 0)
            {
                points.Add(new HarmPoint(0, mass));
                return;
            }

            if (severity >= LethalDamagePerHealthScale * healthScale)
            {
                points.Add(new HarmPoint(DeathLoss(0), mass));
                death += mass;
                return;
            }

            var bleed = Bleeding(severity, out var diedAt, out var bleedDowned);
            if (diedAt is { } at)
            {
                points.Add(new HarmPoint(DeathLoss(at), mass));
                death += mass;
                return;
            }

            var injury = Injury(severity, out var injuryDowned);
            var baseLoss = Math.Min(days, bleed + injury);
            var downed = bleedDowned || injuryDowned;
            var infected = Infection(severity, out var infectionLoss, out var infectionDiedAt, out var infectionDowned);
            if (infected <= 0)
            {
                points.Add(new HarmPoint(baseLoss, mass));
                down += downed ? mass : 0;
                return;
            }

            points.Add(new HarmPoint(baseLoss, mass * (1 - infected)));
            down += downed ? mass * (1 - infected) : 0;
            if (infectionDiedAt is { } infectionAt)
            {
                points.Add(new HarmPoint(DeathLoss(infectionAt), mass * infected));
                death += mass * infected;
                return;
            }

            points.Add(new HarmPoint(Math.Min(days, baseLoss + infectionLoss), mass * infected));
            down += downed || infectionDowned ? mass * infected : 0;
        }

        private double DeathLoss(double atDay) => Math.Max(0, days - atDay) + policy.DeathTerminalValue;

        // Capability-days lost to the blood loss the bleeding builds; diedAt is the day it reaches the lethal severity.
        private double Bleeding(double severity, out double? diedAt, out bool downed)
        {
            diedAt = null;
            downed = false;
            var table = tables.BloodLoss;
            var rate = BleedPerSeverity * severity * mix.BleedSeverity / mix.MeanDamage;
            if (!bleedingPriced || rate < BleedingGiverThreshold)
            {
                return 0;
            }

            var woundSeverity = mix.MeanDamage / mix.Wounds;
            var untended = StopBleedingBaseDays + StopBleedingExtraDays * Math.Clamp((woundSeverity - StopBleedingSeverityLo) / (StopBleedingSeverityHi - StopBleedingSeverityLo), 0, 1);
            var bleeding = Math.Min(Math.Min(untended, tended ? firstCareDays : untended), days);
            if (table.LethalSeverity is { } lethal && rate * bleeding >= lethal)
            {
                diedAt = lethal / rate;
                return 0;
            }

            var course = new ForecastValue.Course(table, levels, movingMin, 0);
            course.Advance(bleeding * GameTime.TicksPerDay, rate);
            course.Advance(horizon - bleeding * GameTime.TicksPerDay, -BloodLossRecoveryPerDay);
            downed = course.Downs;
            return course.LostTicks / GameTime.TicksPerDay;
        }

        // Capability-days lost to pain while the injuries heal: pain takes consciousness, pain shock or a consciousness under
        // the awake level downs the pawn (full loss until the pain falls below it).
        private double Injury(double severity, out bool downed)
        {
            var heal = healthScale * healing;
            var natural = (Pain(severity) >= shock || Level(Pain(severity)) < ForecastValue.AwakeConsciousness ? LyingHealPerDay : NaturalHealPerDay) * heal;
            var extra = tended ? TendedHealPerDay * (TendedHealBase + tendQuality) * heal : 0;
            var untendedRemaining = Math.Max(0, severity - natural * Math.Min(firstCareDays, days));
            var alone = natural <= 0 ? double.PositiveInfinity : severity / natural;
            var healed = Math.Min(days, firstCareDays < alone ? firstCareDays + untendedRemaining / (natural + extra) : alone);
            downed = false;
            var lost = 0d;
            var dt = healed / HealingSteps;
            for (var step = 0; step < HealingSteps; step++)
            {
                var t = (step + 0.5) * dt;
                var remaining = t < firstCareDays ? severity - natural * t : untendedRemaining - (natural + extra) * (t - firstCareDays);
                var pain = Pain(Math.Max(0, remaining));
                var factor = Factor(pain, out var limp);
                downed |= limp;
                lost += (1 - factor) * dt;
            }

            return lost;
        }

        private double Pain(double severity) => PainPerSeverity * severity / healthScale;

        private double Level(double pain) =>
            levels[0] - Math.Clamp((pain - PainFree) / (PainFull - PainFree) * PainMaxPenalty, 0, PainMaxPenalty);

        // The share of capability left at a pain level: 0 when the pain downs the pawn, 1 for one already down.
        private double Factor(double pain, out bool downed)
        {
            downed = false;
            if (levels[0] < ForecastValue.AwakeConsciousness || levels[0] <= 0)
            {
                return 1;
            }

            var level = Level(pain);
            if (pain >= shock || level < ForecastValue.AwakeConsciousness)
            {
                downed = true;
                return 0;
            }

            return level / levels[0];
        }

        // The chance some wound infects and, if so, the capability-days its course loses over the horizon.
        private double Infection(double severity, out double loss, out double? diedAt, out bool downed)
        {
            loss = 0;
            diedAt = null;
            downed = false;
            if (!infectionPriced)
            {
                return 0;
            }

            var table = tables.WoundInfection;
            var immunizable = table.Immunizable!;
            var wounds = severity * mix.Wounds / mix.MeanDamage;
            var chance = InfectionChance
                * (tended ? Lerp(InfectionTendFactorUntendedQuality, InfectionTendFactorFullQuality, tendQuality) * careRoomFactor : 1)
                * Lerp(InfectionSeverityFactorLo, 1, Math.Clamp((mix.MeanDamage / mix.Wounds - InfectionSeverityLo) / (InfectionSeverityHi - InfectionSeverityLo), 0, 1));
            var infected = 1 - Math.Exp(-wounds * chance);
            if (infected <= 0)
            {
                return 0;
            }

            // Severity rises at the not-immune rate until the pawn's immunity (gained at the sick rate) is full, then falls at
            // the immune rate; tending adds severityPerDayTended x tend quality throughout.
            var tending = tended && table.SeverityPerDayTended is { } perDay ? perDay * tendQuality : 0;
            var immuneAt = Math.Min(days, 1 / immunizable.ImmunityPerDaySick);
            var rising = immunizable.SeverityPerDayNotImmune + tending;
            var course = new ForecastValue.Course(table, levels, movingMin, 0) { Severity = table.InitialSeverity };
            if (table.LethalSeverity is { } lethal && table.InitialSeverity + rising * immuneAt >= lethal)
            {
                diedAt = rising > 0 ? Math.Max(0, (lethal - table.InitialSeverity) / rising) : 0;
                return infected;
            }

            course.Advance(immuneAt * GameTime.TicksPerDay, rising);
            course.Advance((days - immuneAt) * GameTime.TicksPerDay, immunizable.SeverityPerDayImmune + tending);
            loss = course.LostTicks / GameTime.TicksPerDay;
            downed = course.Downs;
            return infected;
        }

        private static double Lerp(double from, double to, double t) => from + (to - from) * t;
    }
}
