using System.Collections.Concurrent;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One pawn's illness forecast (#3824): the distribution of one disease incident's cost to it (<see cref="HarmDistribution"/>,
/// with its expected patient-days and tends), and the colony's disease incidents a day, so a day brings
/// <see cref="PatientDaysPerDay"/> days in care, <see cref="TendsPerDay"/> tends and <see cref="DeathsPerDay"/> deaths.
/// </summary>
public sealed record IllnessPawn(string Pawn, HarmDistribution Distribution, double IncidentsPerDay)
{
    public double PatientDaysPerDay => IncidentsPerDay * Distribution.PatientDays;

    public double TendsPerDay => IncidentsPerDay * Distribution.Treatments;

    public double DeathsPerDay => IncidentsPerDay * Distribution.DeathProbability;
}

/// <summary>
/// The illness part of the harm term (#3824): disease incidents a day, the risk-transformed loss over the horizon (incidents a day ×
/// horizon days × Σ over pawns of (1 − w) × E + w × CVaR of the pawn's distribution, as raids) and each pawn's row.
/// </summary>
public sealed record IllnessTerm(double IncidentsPerDay, double Loss, IReadOnlyList<IllnessPawn> Pawns)
{
    public double DeathsPerDay => Pawns.Sum(p => p.DeathsPerDay);

    public double PatientDaysPerDay => Pawns.Sum(p => p.PatientDaysPerDay);

    public double TendsPerDay => Pawns.Sum(p => p.TendsPerDay);
}

/// <summary>
/// The course of a disease from the day it strikes, priced in capability-days from the hediff's own comps (#3824): severity moves
/// at the Immunizable comp's not-immune rate (times its random factor) until the pawn's immunity, gained at immunityPerDaySick ×
/// ImmunityGainSpeed × the record's seeded factor, is full, then at the immune rate; the SeverityPerDay comp adds its rate; tending
/// adds severityPerDayTended × tend quality from first care, retended as soon as the TendDuration comp allows. The hediff ends when
/// its severity reaches 0, the Disappears comp's time passes or the total tend quality reaches disappearsAtTotalTendQuality; it
/// kills at its lethal severity on a part it threatens; at the max severity HediffComp_DestroyOrgan destroys the part. Uniform
/// draws (the random factors, the per-day range, the disappear time, the immunity factor) are integrated on a midpoint grid; the
/// death chance in the immunity factor is exact. The constants it mirrors are in kept-constants, "Mirrored game literals: illness".
/// </summary>
public static class IllnessForecast
{
    // ImmunityRecord.ImmunityChangePerTick: immunityPerDaySick × Lerp(0.8, 1.2, a seeded Rand.Value) while sick.
    internal const double ImmunityFactorMin = 0.8;
    internal const double ImmunityFactorMax = 1.2;

    /// <summary>The points of each uniform draw's midpoint grid.</summary>
    internal const int GridPoints = 4;

    private const double Epsilon = 1e-9;

    /// <summary>
    /// The illness term of <paramref name="ordered"/> pawns tended by <paramref name="support"/> over <paramref name="horizon"/> ticks;
    /// null, with "illness_unassessed" and the reason in <paramref name="findings"/>, when the schedule or a pawn's facts are absent.
    /// </summary>
    internal static IllnessTerm? Term(IllnessSchedule schedule, IReadOnlyList<string> ordered, IReadOnlyDictionary<string, NeedsForecastPawn> pawns,
        CapacityPenaltyTables tables, ForecastSupportConsequences? support, ForecastValuePolicy policy, double horizon,
        ConcurrentDictionary<(string, ForecastSupportConsequences?), HarmDistribution>? cache, SortedSet<string> findings)
    {
        if (!schedule.Assessed)
        {
            findings.Add("illness_unassessed");
            findings.UnionWith(schedule.Findings);
            return null;
        }

        var days = horizon / GameTime.TicksPerDay;
        var perDay = schedule.IncidentsPerDay(days);
        var rows = new List<IllnessPawn>();
        var risked = 0.0;
        var assessed = true;
        foreach (var pawn in ordered)
        {
            if (!pawns.TryGetValue(pawn, out var row))
            {
                assessed = false;
                findings.Add("illness_unassessed");
                findings.Add("capacities_unobserved:" + pawn);
                continue;
            }

            var key = (pawn, support);
            if (cache is null || !cache.TryGetValue(key, out var distribution))
            {
                // A sick pawn rests in the bed it owns (#3804): its immunity gains at that bed's ImmunityGainSpeedFactor.
                distribution = Of(schedule, row, tables, HarmConsequences.CareOf(support), row.OwnedBedRoom?.ImmunityGainSpeedFactor ?? 1, policy, horizon);
                if (cache is not null)
                {
                    cache[key] = distribution;
                }
            }

            findings.UnionWith(distribution.Findings);
            if (!distribution.Known)
            {
                assessed = false;
                findings.Add("illness_unassessed");
                continue;
            }

            var (mean, tail) = HarmLedger.Moments(distribution, policy.HarmRiskAlpha);
            risked += HarmLedger.Risk(mean, tail, policy.HarmRiskWeight);
            rows.Add(new IllnessPawn(pawn, distribution, perDay));
        }

        return assessed ? new IllnessTerm(perDay, perDay * days * risked, rows) : null;
    }

    /// <summary>
    /// What one disease incident costs <paramref name="pawn"/> given <paramref name="care"/>, its immunity gained in a bed of
    /// <paramref name="bedImmunity"/> ImmunityGainSpeedFactor (StatPart_BedStat, #3804; 1 without a bed): for each disease, its share
    /// × the chance the pawn is a victim (drawn and contracting it) × its course, and the rest a point at 0. Unknown when the pawn's
    /// illness reads, capacities or the Moving minimum are unobserved.
    /// </summary>
    internal static HarmDistribution Of(IllnessSchedule schedule, NeedsForecastPawn pawn, CapacityPenaltyTables tables,
        HarmConsequences.Care care, double bedImmunity, ForecastValuePolicy policy, double horizon)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        if (pawn.Disease is not { } disease || !double.IsFinite(disease.ImmunityGainSpeed))
        {
            findings.Add("illness_unobserved:" + pawn.Pawn);
            return new HarmDistribution { Findings = [.. findings] };
        }

        if (ForecastValue.Levels(pawn) is not { } levels)
        {
            findings.Add("capacities_unobserved:" + pawn.Pawn);
            return new HarmDistribution { Findings = [.. findings] };
        }

        if (tables.MovingMinForCapable is not { } movingMin)
        {
            findings.Add("capacity_penalty_table_unknown:MovingMinForCapable");
            return new HarmDistribution { Findings = [.. findings] };
        }

        if (!care.Tended)
        {
            findings.Add("illness_untended");
        }

        // An earlier immunity record is not in the pawn's read.
        findings.Add("illness_prior_immunity_unassessed");
        var days = horizon / GameTime.TicksPerDay;
        var points = new List<HarmPoint>();
        double none = 1, down = 0, death = 0, patientDays = 0, tends = 0;
        foreach (var incident in schedule.Incidents)
        {
            var contract = -1.0;
            foreach (var row in disease.Contracts)
            {
                if (row.Incident == incident.Incident)
                {
                    contract = row.Chance;
                }
            }

            if (!(contract >= 0))
            {
                findings.Add("disease_contract_unobserved:" + pawn.Pawn + ":" + incident.Incident);
                return new HarmDistribution { Findings = [.. findings] };
            }

            var sick = incident.Share * incident.VictimChance * Math.Clamp(contract, 0, 1);
            if (!(sick > 0))
            {
                continue;
            }

            none -= sick;
            var course = Course(incident.Course, disease.ImmunityGainSpeed * Math.Max(0, bedImmunity), care, levels, movingMin, policy, days, pawn.Pawn, findings);
            foreach (var outcome in course)
            {
                points.Add(new HarmPoint(outcome.Loss, sick * outcome.Probability));
                down += outcome.Downed ? sick * outcome.Probability : 0;
                death += outcome.Died ? sick * outcome.Probability : 0;
                patientDays += sick * outcome.Probability * outcome.PatientDays;
                tends += sick * outcome.Probability * outcome.Tends;
            }
        }

        if (none > 0)
        {
            points.Add(new HarmPoint(0, none));
        }

        return new HarmDistribution
        {
            Known = true, Points = points, DownProbability = down, DeathProbability = death, PatientDays = patientDays, Treatments = tends,
            Findings = [.. findings],
        };
    }

    /// <summary>One outcome of a course: its capability-day loss and probability, and its days in care and tends.</summary>
    internal readonly record struct CourseOutcome(double Loss, double Probability, bool Died, bool Downed, double PatientDays, double Tends);

    /// <summary>The outcomes of <paramref name="course"/> for a pawn of <paramref name="gain"/> ImmunityGainSpeed; probabilities sum to 1.</summary>
    internal static List<CourseOutcome> Course(DiseaseCourse course, double gain, HarmConsequences.Care care, double[] levels, double movingMin,
        ForecastValuePolicy policy, double days, string pawn, SortedSet<string> findings)
    {
        var outcomes = new List<CourseOutcome>();
        findings.UnionWith(course.Unpriced);
        var immunizable = course.Table.Immunizable;
        var immunityRate = immunizable is null ? 0 : Math.Max(0, immunizable.ImmunityPerDaySick * gain);
        var gone = course.Table.DisappearsAfterTicks is { } ticks ? (ticks.Min / GameTime.TicksPerDay, ticks.Max / GameTime.TicksPerDay) : (double.PositiveInfinity, double.PositiveInfinity);
        var lethalShare = course.Table.LethalSeverity is null ? 0 : course.LethalPartShare;
        foreach (var (lethal, branch) in new[] { (true, lethalShare), (false, 1 - lethalShare) })
        {
            if (!(branch > 0))
            {
                continue;
            }

            foreach (var (factor, fw) in Grid(course.NotImmuneFactorMin, course.NotImmuneFactorMax))
            {
                foreach (var (perDay, pw) in Grid(course.PerDayMin, course.PerDayMax))
                {
                    foreach (var (goneAt, gw) in Grid(gone.Item1, gone.Item2))
                    {
                        var weight = branch * fw * pw * gw;
                        var run = new Run(course, care, factor, perDay, goneAt, lethal, levels, movingMin, days);
                        if (!(immunityRate > 0))
                        {
                            Add(outcomes, run.Walk(double.PositiveInfinity), weight, policy, days, course, pawn, findings);
                            continue;
                        }

                        // Death before immunity does not depend on when immunity comes: the course without immunity dies at a day
                        // tL, and the pawn dies when its immunity day 1 / (rate × u) is not earlier, so for u ≤ 1 / (rate × tL).
                        var lo = ImmunityFactorMin;
                        var never = run.Walk(double.PositiveInfinity);
                        if (never.Died)
                        {
                            var threshold = 1 / (immunityRate * Math.Max(never.End, Epsilon));
                            var dies = Math.Clamp((threshold - ImmunityFactorMin) / (ImmunityFactorMax - ImmunityFactorMin), 0, 1);
                            if (dies > 0)
                            {
                                Add(outcomes, never, weight * dies, policy, days, course, pawn, findings);
                            }

                            lo = Math.Clamp(threshold, ImmunityFactorMin, ImmunityFactorMax);
                            weight *= 1 - dies;
                        }

                        if (!(weight > 0) || lo >= ImmunityFactorMax)
                        {
                            continue;
                        }

                        foreach (var (u, uw) in Grid(lo, ImmunityFactorMax))
                        {
                            Add(outcomes, run.Walk(1 / (immunityRate * u)), weight * uw, policy, days, course, pawn, findings);
                        }
                    }
                }
            }
        }

        return outcomes;
    }

    private static void Add(List<CourseOutcome> outcomes, Walked walked, double probability, ForecastValuePolicy policy, double days,
        DiseaseCourse course, string pawn, SortedSet<string> findings)
    {
        walked.Report(pawn, findings);
        if (walked.OrganLost)
        {
            findings.Add("disease_organ_loss_unpriced:" + course.Table.Hediff);
        }

        var loss = walked.Died ? Math.Max(0, days - walked.End) + policy.DeathTerminalValue : Math.Min(days, walked.LostDays);
        outcomes.Add(new CourseOutcome(loss, probability, walked.Died, walked.Downed, walked.PatientDays, walked.Tends));
    }

    /// <summary>The midpoint grid of a uniform range: one point for a degenerate or infinite range.</summary>
    internal static IEnumerable<(double Value, double Weight)> Grid(double min, double max)
    {
        if (!(max > min) || !double.IsFinite(max - min))
        {
            yield return (min, 1);
            yield break;
        }

        for (var i = 0; i < GridPoints; i++)
        {
            yield return (min + ((i + 0.5) * (max - min) / GridPoints), 1d / GridPoints);
        }
    }

    /// <summary>What a walked course ended in.</summary>
    internal sealed class Walked
    {
        public bool Died;
        public bool OrganLost;
        public bool Downed;
        public double End;
        public double LostDays;
        public double PatientDays;
        public int Tends;
        public ForecastValue.Course Severity;

        public void Report(string pawn, SortedSet<string> findings) => Severity.Report(pawn, findings);
    }

    /// <summary>One draw of a course: its random factors fixed; <see cref="Walk"/> runs it to the day immunity comes.</summary>
    private readonly struct Run(DiseaseCourse course, HarmConsequences.Care care, double factor, double perDay, double goneAt, bool lethal,
        double[] levels, double movingMin, double days)
    {
        public Walked Walk(double immuneAt)
        {
            var table = course.Table;
            var immunizable = table.Immunizable;
            var tend = care.Tended && course.Tendable ? course.Tend : null;
            var careAt = tend is null ? double.PositiveInfinity : Math.Max(0, care.FirstCareDays);
            var tended = tend is null ? 0 : tend.SeverityPerDayTended * care.Quality;
            var lethalAt = lethal && table.LethalSeverity is { } l ? l : double.PositiveInfinity;
            var top = Math.Min(lethalAt, course.MaxSeverity);
            var bottom = Math.Max(0, course.MinSeverity);
            var walked = new Walked { Severity = new ForecastValue.Course(table, levels, movingMin, 0) };
            var s = Math.Clamp(table.InitialSeverity, course.MinSeverity, course.MaxSeverity);
            var t = 0.0;
            var quality = 0.0;
            var nextTend = careAt;
            for (var guard = 0; guard < 100000; guard++)
            {
                if (t >= days - Epsilon || t >= goneAt - Epsilon)
                {
                    return Finish(walked, Math.Min(t, days), careAt, died: false);
                }

                if (tend is not null && t >= nextTend - Epsilon)
                {
                    walked.Tends++;
                    quality += care.Quality;
                    if (tend.CureAtTotalQuality >= 0 && quality >= tend.CureAtTotalQuality - Epsilon)
                    {
                        return Finish(walked, t, careAt, died: false);
                    }

                    nextTend = t + (walked.Tends == 1 ? tend.FirstRetendDays : tend.RetendDays);
                    continue;
                }

                var rate = (immunizable is null ? 0 : t >= immuneAt - Epsilon ? immunizable.SeverityPerDayImmune : immunizable.SeverityPerDayNotImmune * factor)
                    + perDay + (t >= careAt - Epsilon ? tended : 0);
                var until = Math.Min(Math.Min(days, goneAt), nextTend);
                if (t < immuneAt - Epsilon)
                {
                    until = Math.Min(until, immuneAt);
                }

                var effective = rate;
                var crossing = false;
                if (rate > 0 && s >= top - Epsilon || rate < 0 && s <= bottom + Epsilon)
                {
                    if (rate < 0 && bottom <= 0)
                    {
                        // Hediff.ShouldRemove: the severity is at 0.
                        return Finish(walked, t, careAt, died: false);
                    }

                    effective = 0;
                }
                else if (rate != 0)
                {
                    var at = t + ((rate > 0 ? top - s : s - bottom) / Math.Abs(rate));
                    if (at <= until)
                    {
                        until = at;
                        crossing = true;
                    }
                }

                walked.Severity.Severity = s;
                walked.Severity.Advance((until - t) * GameTime.TicksPerDay, effective);
                s = crossing ? rate > 0 ? top : bottom : Math.Clamp(s + (effective * (until - t)), course.MinSeverity, course.MaxSeverity);
                t = until;
                if (!crossing)
                {
                    continue;
                }

                if (rate > 0 && s >= lethalAt - Epsilon)
                {
                    return Finish(walked, t, careAt, died: true);
                }

                if (rate > 0 && course.DestroysOrgan && s >= course.MaxSeverity - Epsilon)
                {
                    walked.OrganLost = true;
                    return Finish(walked, t, careAt, died: false);
                }

                if (rate < 0 && bottom <= 0)
                {
                    return Finish(walked, t, careAt, died: false);
                }
            }

            return Finish(walked, Math.Min(t, days), careAt, died: false);
        }

        private Walked Finish(Walked walked, double end, double careAt, bool died)
        {
            walked.Died = died;
            walked.End = end;
            walked.LostDays = walked.Severity.LostTicks / GameTime.TicksPerDay;
            walked.Downed = walked.Severity.Downs;
            walked.PatientDays = double.IsFinite(careAt) ? Math.Max(0, Math.Min(end, days) - careAt) : 0;
            return walked;
        }
    }
}
