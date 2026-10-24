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
/// kills at its lethal severity on a part it threatens; at the max severity HediffComp_DestroyOrgan destroys the part. A disease
/// of affected parts strikes one of the pawn's open parts by coverage (HediffGiverUtility.TryApply); on an organ its stages'
/// partEfficiencyOffset and its loss cost what <see cref="OrganCapacity"/> gives (#3830). A lost organ the autopilot would replace
/// (<see cref="SurgeryRules.RestorePick"/>: a stocked recipe and a doctor within the cap) costs nothing after the loss and books a
/// replacement surgery (<see cref="HarmDistribution.Replacements"/>); one it would not costs its capacity loss to the horizon; a loss
/// that leaves a lethalFlesh capacity incapable kills. Uniform draws (the random factors, the per-day range, the disappear time,
/// the immunity factor) are integrated on a midpoint grid; the death chance in the immunity factor is exact. The constants it
/// mirrors are in kept-constants, "Mirrored game literals: illness".
/// </summary>
public static class IllnessForecast
{
    // ImmunityRecord.ImmunityChangePerTick: immunityPerDaySick × Lerp(0.8, 1.2, a seeded Rand.Value) while sick.
    internal const double ImmunityFactorMin = 0.8;
    internal const double ImmunityFactorMax = 1.2;

    /// <summary>The points of each uniform draw's midpoint grid.</summary>
    internal const int GridPoints = 4;

    private const double Epsilon = 1e-9;

    /// <summary>The finding of a colony whose illness the disease table does not model (its storyteller, biome, incident or course): the term then holds no illness.</summary>
    public const string Unmodelled = "illness_unmodelled";

    /// <summary>
    /// The illness term of <paramref name="ordered"/> pawns tended by <paramref name="support"/> over <paramref name="horizon"/> ticks;
    /// null, with <see cref="Unmodelled"/> and the reason in <paramref name="findings"/>, when the table does not model the colony's
    /// illness. A schedule fact or a pawn's fact left unread throws <see cref="RequiredFactException"/> (#3835).
    /// </summary>
    internal static IllnessTerm? Term(IllnessSchedule schedule, IReadOnlyList<string> ordered, IReadOnlyDictionary<string, NeedsForecastPawn> pawns,
        CapacityPenaltyTables tables, ForecastSupportConsequences? support, ForecastValuePolicy policy, double horizon,
        ConcurrentDictionary<(string, ForecastSupportConsequences?), HarmDistribution>? cache, SortedSet<string> findings)
    {
        if (!schedule.Assessed)
        {
            if (schedule.Unread) throw new RequiredFactException(string.Join(",", schedule.Findings));
            findings.Add(Unmodelled);
            findings.UnionWith(schedule.Findings);
            return null;
        }

        findings.UnionWith(schedule.Findings);
        var days = horizon / GameTime.TicksPerDay;
        var perDay = schedule.IncidentsPerDay(days);
        var rows = new List<IllnessPawn>();
        var risked = 0.0;
        foreach (var pawn in ordered)
        {
            if (!pawns.TryGetValue(pawn, out var row))
            {
                throw new RequiredFactException("capacities_unobserved:" + pawn);
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
                throw new RequiredFactException(string.Join(",", distribution.Findings));
            }

            var (mean, tail) = HarmLedger.Moments(distribution, policy.HarmRiskAlpha);
            risked += HarmLedger.Risk(mean, tail, policy.HarmRiskWeight);
            rows.Add(new IllnessPawn(pawn, distribution, perDay));
        }

        return new IllnessTerm(perDay, perDay * days * risked, rows);
    }

    /// <summary>
    /// What one disease incident costs <paramref name="pawn"/> given <paramref name="care"/>, its immunity gained in a bed of
    /// <paramref name="bedImmunity"/> ImmunityGainSpeedFactor (StatPart_BedStat, #3804; 1 without a bed): for each disease, its share
    /// × the chance the pawn is a victim (drawn and contracting it) × its course, and the rest a point at 0. Unknown when the Moving
    /// minimum is unobserved.
    /// </summary>
    internal static HarmDistribution Of(IllnessSchedule schedule, NeedsForecastPawn pawn, CapacityPenaltyTables tables,
        HarmConsequences.Care care, double bedImmunity, ForecastValuePolicy policy, double horizon)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var (disease, levels) = (pawn.Disease, ForecastValue.Levels(pawn));

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
        var replacements = new Dictionary<(string, string, SurgeryWantReason), OrganReplacement>();
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

            var branches = Branches(incident, disease, levels, pawn.Pawn, findings);
            sick *= branches.Sum(b => b.Share);
            if (!(sick > 0))
            {
                continue;
            }

            none -= sick;
            var course = Course(incident.Course, branches, disease.ImmunityGainSpeed * Math.Max(0, bedImmunity), care, levels, movingMin, policy, days, pawn.Pawn, findings);
            foreach (var outcome in course)
            {
                points.Add(new HarmPoint(outcome.Loss, sick * outcome.Probability));
                down += outcome.Downed ? sick * outcome.Probability : 0;
                death += outcome.Died ? sick * outcome.Probability : 0;
                patientDays += sick * outcome.Probability * outcome.PatientDays;
                tends += sick * outcome.Probability * outcome.Tends;
                if (outcome.Replacement is { } lost)
                {
                    var key = (lost.Part, lost.Recipe?.Recipe ?? "", lost.Reason);
                    var probability = sick * outcome.Probability + (replacements.TryGetValue(key, out var seen) ? seen.Probability : 0);
                    replacements[key] = lost with { Probability = probability };
                }
            }
        }

        if (none > 0)
        {
            points.Add(new HarmPoint(0, none));
        }

        return new HarmDistribution
        {
            Known = true, Points = points, DownProbability = down, DeathProbability = death, PatientDays = patientDays, Treatments = tends,
            Replacements = [.. replacements.Values.OrderBy(r => r.Part, StringComparer.Ordinal).ThenBy(r => r.Recipe?.Recipe ?? "", StringComparer.Ordinal).ThenBy(r => r.Reason)],
            Findings = [.. findings],
        };
    }

    /// <summary>One outcome of a course: its capability-day loss and probability, its days in care and tends, and the organ it destroyed (#3830).</summary>
    internal readonly record struct CourseOutcome(double Loss, double Probability, bool Died, bool Downed, double PatientDays, double Tends, OrganReplacement? Replacement);

    /// <summary>
    /// One part a disease may strike (#3830): its share of the incidents that take hold, whether the lethal severity kills on it, and
    /// its organ's capacity effect (null when the part feeds no modelled capacity, or for a disease of the whole body).
    /// </summary>
    internal sealed record Branch(double Share, bool Lethal, OrganEffect? Organ);

    /// <summary>
    /// An organ's capacity effect (#3830): per stage of the course's table the factors its partEfficiencyOffset leaves on the five
    /// levels (null for a stage that offsets nothing), the factors once the organ is lost (null: the loss kills), and the replacement
    /// the autopilot would queue (null, with the reason, when none would be).
    /// </summary>
    internal sealed record OrganEffect(string Part, double[]?[] Stages, double[]? Lost, OrganReplacementRead? Pick, SurgeryWantReason Reason);

    /// <summary>
    /// The parts <paramref name="incident"/>'s disease may strike on the pawn: one branch of share 1 for a disease of the whole body;
    /// otherwise, as HediffGiverUtility.TryApply picks, each part the read lists as open to the incident by its coverage, the shares
    /// summing to 1 (none when no part is open: the disease does not take). Throws when the pawn's read lists no part of the
    /// incident's defs, or names a replacement recipe the catalog has no tier for.
    /// </summary>
    internal static IReadOnlyList<Branch> Branches(IllnessIncident incident, PawnDiseaseFacts disease, double[] levels, string pawn, SortedSet<string> findings)
    {
        var course = incident.Course;
        var lethal = course.Table.LethalSeverity is not null;
        if (course.Parts.Count == 0)
        {
            return [new Branch(1, lethal, null)];
        }

        var defs = course.Parts.ToDictionary(p => p.Part, StringComparer.Ordinal);
        var rows = disease.Parts.Where(r => defs.ContainsKey(r.Part)).ToArray();
        if (rows.Length == 0)
        {
            throw new RequiredFactException("disease_parts:" + pawn + ":" + incident.Incident);
        }

        var open = rows.Where(r => r.Incidents.Contains(incident.Incident) && r.Coverage > 0).ToArray();
        var cover = open.Sum(r => r.Coverage);
        var branches = new List<Branch>(open.Length);
        foreach (var row in open)
        {
            var part = defs[row.Part];
            branches.Add(new Branch(row.Coverage / cover, lethal && part.Threatened, Organ(course, part, row, rows, disease, levels, findings)));
        }

        return branches;
    }

    private static OrganEffect? Organ(DiseaseCourse course, DiseasePart part, DiseasePartRead row, DiseasePartRead[] rows, PawnDiseaseFacts disease,
        double[] levels, SortedSet<string> findings)
    {
        if (part.Capacity.Length == 0)
        {
            return null;
        }

        // The capacity names are PawnCapacityDef defNames the catalog maps the tags to; the policy has no typed route to them.
        var level = part.Capacity switch
        {
            "BloodPumping" => disease.BloodPumping,
            "Breathing" => disease.Breathing,
            "BloodFiltration" => disease.BloodFiltration,
            _ => throw new RequiredFactException("organ_capacity:" + part.Capacity),
        };
        var tagged = rows.Where(r => course.Parts.Any(p => p.Part == r.Part && p.Tag == part.Tag)).ToArray();
        var efficiencies = tagged.Select(r => r.Efficiency).ToArray();
        var index = Array.IndexOf(tagged, row);
        double[]? At(double efficiency)
        {
            var next = OrganCapacity.Level(level, efficiencies, index, efficiency);
            return part.Lethal && next <= part.MinForCapable ? null : OrganCapacity.Factors(part.Capacity, level, next, levels[0]);
        }

        var stages = new double[]?[course.Table.Stages.Count];
        for (var i = 0; i < stages.Length; i++)
        {
            var offset = course.Table.Stages[i].PartEfficiencyOffset;
            if (offset == 0)
            {
                continue;
            }

            if (At(Math.Max(0, row.Efficiency + offset)) is { } factors)
            {
                stages[i] = factors;
                continue;
            }

            // A stage that leaves a lethalFlesh capacity incapable kills at once (Pawn_HealthTracker.ShouldBeDead); the walk prices
            // it as downed for the stage's stretch.
            findings.Add("disease_part_stage_death_unmodelled:" + course.Table.Hediff);
            stages[i] = new double[levels.Length];
        }

        if (!course.DestroysOrgan)
        {
            return new OrganEffect(part.Part, stages, null, null, SurgeryWantReason.None);
        }

        var candidates = disease.Replacements.Where(r => r.Part == part.Part)
            .Select(r => (r, part.Recipes.TryGetValue(r.Recipe, out var tier) ? tier : throw new RequiredFactException("organ_replacement_tier:" + r.Recipe)))
            .ToArray();
        var (pick, reason) = SurgeryRules.RestorePick(candidates);
        return new OrganEffect(part.Part, stages, At(0), pick, reason);
    }

    /// <summary>The outcomes of <paramref name="course"/> over its <paramref name="branches"/> (shares normalised) for a pawn of <paramref name="gain"/> ImmunityGainSpeed; probabilities sum to 1.</summary>
    internal static List<CourseOutcome> Course(DiseaseCourse course, IReadOnlyList<Branch> branches, double gain, HarmConsequences.Care care, double[] levels, double movingMin,
        ForecastValuePolicy policy, double days, string pawn, SortedSet<string> findings)
    {
        var outcomes = new List<CourseOutcome>();
        findings.UnionWith(course.Unpriced);
        var immunizable = course.Table.Immunizable;
        var immunityRate = immunizable is null ? 0 : Math.Max(0, immunizable.ImmunityPerDaySick * gain);
        var gone = course.Table.DisappearsAfterTicks is { } ticks ? (ticks.Min / GameTime.TicksPerDay, ticks.Max / GameTime.TicksPerDay) : (double.PositiveInfinity, double.PositiveInfinity);
        var total = branches.Sum(b => b.Share);
        foreach (var branch in branches)
        {
            if (!(branch.Share > 0))
            {
                continue;
            }

            var lethal = branch.Lethal;
            var organ = branch.Organ;
            foreach (var (factor, fw) in Grid(course.NotImmuneFactorMin, course.NotImmuneFactorMax))
            {
                foreach (var (perDay, pw) in Grid(course.PerDayMin, course.PerDayMax))
                {
                    foreach (var (goneAt, gw) in Grid(gone.Item1, gone.Item2))
                    {
                        var weight = branch.Share / total * fw * pw * gw;
                        var run = new Run(course, care, factor, perDay, goneAt, lethal, organ, levels, movingMin, days);
                        if (!(immunityRate > 0))
                        {
                            Add(outcomes, run.Walk(double.PositiveInfinity), weight, policy, days, course, organ, levels, movingMin, pawn, findings);
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
                                Add(outcomes, never, weight * dies, policy, days, course, organ, levels, movingMin, pawn, findings);
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
                            Add(outcomes, run.Walk(1 / (immunityRate * u)), weight * uw, policy, days, course, organ, levels, movingMin, pawn, findings);
                        }
                    }
                }
            }
        }

        return outcomes;
    }

    // A course that ends in organ loss (#3830): a loss that leaves a lethalFlesh capacity incapable kills; one the autopilot replaces
    // costs nothing after it (the wait for the surgery is named, not priced); one it does not costs the lost organ's capability
    // factor to the horizon.
    private static void Add(List<CourseOutcome> outcomes, Walked walked, double probability, ForecastValuePolicy policy, double days,
        DiseaseCourse course, OrganEffect? organ, double[] levels, double movingMin, string pawn, SortedSet<string> findings)
    {
        walked.Report(pawn, findings);
        var died = walked.Died;
        var after = 0.0;
        OrganReplacement? replacement = null;
        if (walked.OrganLost && organ is null)
        {
            findings.Add("disease_part_capacity_unmodelled:" + course.Table.Hediff);
        }
        else if (walked.OrganLost)
        {
            (died, after, replacement) = Lost(walked, organ!, probability, levels, movingMin, days, findings);
        }

        var loss = died ? Math.Max(0, days - walked.End) + policy.DeathTerminalValue : Math.Min(days, walked.LostDays + after);
        outcomes.Add(new CourseOutcome(loss, probability, died, walked.Downed, walked.PatientDays, walked.Tends, replacement));
    }

    private static (bool Died, double After, OrganReplacement? Replacement) Lost(Walked walked, OrganEffect organ, double probability, double[] levels,
        double movingMin, double days, SortedSet<string> findings)
    {
        if (organ.Lost is not { } lost)
        {
            return (true, 0, null);
        }

        var replacement = new OrganReplacement(organ.Part, organ.Pick, organ.Reason, probability);
        if (organ.Pick is not null)
        {
            findings.Add("illness_organ_replacement_wait_unmodelled");
            return (false, 0, replacement);
        }

        Span<double> left = stackalloc double[levels.Length];
        for (var i = 0; i < levels.Length; i++)
        {
            left[i] = levels[i] * lost[i];
        }

        return (false, Math.Max(0, days - walked.End) * (1 - ForecastValue.CapabilityFactor(levels, left, movingMin)), replacement);
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
    private readonly struct Run(DiseaseCourse course, HarmConsequences.Care care, double factor, double perDay, double goneAt, bool lethal, OrganEffect? organ,
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
            var walked = new Walked { Severity = new ForecastValue.Course(table, levels, movingMin, 0) { StageFactors = organ?.Stages } };
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
