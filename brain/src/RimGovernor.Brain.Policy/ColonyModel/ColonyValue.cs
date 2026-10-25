using System.Collections.Concurrent;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

// The value of a tier-2 trajectory in capability-days (#3531 of #3470), priced by the same tables the tier-1 ledger uses: the
// malnutrition stage table for starvation, the harm model for the apparel the chain makes. Higher is better. The marginal value
// of a project is the difference of two of these (ProjectMarginalValue), so a part that is unassessed in both runs cancels.

/// <summary>A trajectory's value in capability-days (higher is better), and the parts left unassessed.</summary>
public sealed record ColonyValueResult(double Value, EquatableArray<string> Findings)
{
    /// <summary>
    /// The <see cref="ProjectMarginals.PerUnit"/> keys (skill stocks, medicine kinds) whose worth this value leaves out (#3635): their
    /// marginal is not a number, not zero, and <see cref="Findings"/> say why.
    /// </summary>
    public EquatableArray<string> Unpriced { get; init; }
}

/// <summary>
/// Prices trajectories under one review's context. The value is the apparel harm avoided (<see cref="ApparelGain"/>: each garment
/// worn from the day it is made to the horizon, credited with its gain over what its wearer wore before) less the capability lost
/// to starvation: the colony's starvation spans walk each pawn's malnutrition severity through the game's stage table from the
/// observed severity, and a pawn whose severity reaches lethal costs the rest of the horizon plus
/// <see cref="ForecastValuePolicy.DeathTerminalValue"/>, as the ledger's thermal term does.
/// </summary>
public sealed class ColonyValue(ForecastCoupledContext context, IReadOnlyList<GearOption> options, double horizonDays,
    EquatableArray<ModelPawn> doctors, ItemFacts medicine) : ITrajectoryValue
{
    /// <summary>The finding of apparel the model cannot price: the harm model gave no number, or no gear option matches the product.</summary>
    public const string ApparelHarmUnassessed = "apparel_harm_unassessed";

    /// <summary>The finding of a pawn who may tend but has no native tend quality curve (#3633), or of a pawn census the value cannot read.</summary>
    public const string MedicineSkillValueUnassessed = "medicine_skill_value_unassessed";

    /// <summary>Raid wounds and illnesses are tended at once: the delay to first care is not modelled.</summary>
    public const string RaidFirstCareDelayUnmodelled = "raid_first_care_delay_unmodelled";

    // StatDefOf.MedicalTendQuality's defName, which NativeSkillStat names the biography's curve by; Policy has no typed route to StatDefOf.
    internal const string TendQualityStat = "MedicalTendQuality";

    // The one care service the tier-2 tending stands for (HarmConsequences averages the quality over the services).
    private const string TendCare = "colony/tend";

    // The stub outcome the harm ledger prices a single pawn's outfit with: it reads the pawn set and the support only.
    private static readonly ForecastNeedsResult NoNeeds = new([], new ForecastTerm.Unassessed("not_projected"), new ForecastTerm.Unassessed("not_projected"), new ForecastCapacityResult(default!, []), []);

    private readonly ConcurrentDictionary<HarmFightKey, HarmDistribution> harmCache = new();
    private readonly Dictionary<string, NeedsForecastPawn> rows = context.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    // The perturbation runs walk the same outfits: one review's losses are priced once.
    private readonly Dictionary<(string, EquatableArray<GearOption>), LedgerTerm> losses = [];

    // The tending term's own cache: its raids are priced over the whole horizon, the apparel's over a day, and the keys omit it.
    private readonly ConcurrentDictionary<HarmFightKey, HarmDistribution> tendedCache = new();

    // The illness distributions of the two horizons (#3824), each by pawn and care.
    private readonly ConcurrentDictionary<IllnessHarmKey, HarmDistribution> tendedIllness = new();
    private readonly ConcurrentDictionary<IllnessHarmKey, HarmDistribution> dailyIllness = new();

    // The colony's raid and illness harm over the horizon at a tend quality (-1: untended) and a defense shift, with the harm model's findings.
    private readonly Dictionary<(double Quality, double Shift), HarmTerm> tendedHarm = [];

    // The skills whose stored level moves the defense capacity (#3852): the sparring ring's Melee and the training range's Shooting.
    private const string MeleeSkill = "Melee", ShootingSkill = "Shooting";

    public ColonyValueResult Price(ColonyTrajectory trajectory)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var unpriced = new SortedSet<string>(StringComparer.Ordinal);
        var hunger = HungerLoss(trajectory, findings);
        var apparel = ApparelGain(trajectory, findings);
        var harm = TendedHarm(trajectory, findings, unpriced);
        return new(apparel - hunger - (harm ?? 0), EquatableArray.From(findings)) { Unpriced = EquatableArray.From(unpriced) };
    }

    /// <summary>
    /// The skill prices are the tended harm's (<see cref="TendedHarm"/>): it reads a doctor's stock of its tend quality curve's skill
    /// (none while neither a raid nor a disease is to be expected), and a fighter's Melee or Shooting stock (<see cref="Fighter"/>,
    /// #3852; none while no raid is to be expected).
    /// </summary>
    public bool Reads(SkillTrack track) => HarmExpected
        && doctors.Any(p => p.Pawn == track.Pawn && p.SkillStatCurves.FirstOrDefault(c => c.Stat == TendQualityStat)?.Skill == track.Skill)
        || HarmLedger.RaidsPerDay(context.Harm, horizonDays) is not 0 && Fighter(track) is not null;

    /// <summary>
    /// The defense power of <paramref name="track"/>'s pawn at each stored level when the stock is one the defense capacity counts
    /// (#3852): a Melee stock of a sparring trainee (MeleeDPS x health) or a Shooting stock of a training range trainee (ranged DPS
    /// at its hit factor), the pawns the one practice eligibility rule admits (<see cref="ForecastCoupledContext.Sparring"/>,
    /// <see cref="ForecastCoupledContext.Range"/>). Null for any other stock: a pawn incapable of the skill, or without its curve
    /// (a named not-modelled finding of the practice read), adds no defense by it. Unread trainees or a curve that does not cover
    /// the levels raise <see cref="RequiredFactException"/>.
    /// </summary>
    private EquatableArray<double>? Fighter(SkillTrack track)
    {
        if (track.Skill is not (MeleeSkill or ShootingSkill))
        {
            return null;
        }

        var fact = track.Skill.ToLowerInvariant();
        var trainees = (track.Skill == MeleeSkill ? context.Sparring : context.Range) ?? throw new RequiredFactException("work pawns");
        if (trainees.Trainees.FirstOrDefault(t => t.Pawn == track.Pawn) is not { } trainee)
        {
            return null;
        }

        return trainee.Power.Count == trainee.Learning.Levels.Count && track.StoredLevel < trainee.Power.Count ? trainee.Power
            : throw new RequiredFactException(fact + " power curve " + track.Pawn);
    }

    /// <summary>
    /// The fighters' stocks and power curves (<see cref="Fighter"/>) of the run while a raid is to be expected; none without one,
    /// when no level of theirs changes the harm.
    /// </summary>
    private List<(SkillTrack Track, EquatableArray<double> Power)> Fighters(ColonyTrajectory trajectory)
    {
        var fighters = new List<(SkillTrack, EquatableArray<double>)>();
        if (HarmLedger.RaidsPerDay(context.Harm, horizonDays) is 0)
        {
            return fighters;
        }

        foreach (var track in trajectory.Skills)
        {
            if (Fighter(track) is { } power)
            {
                fighters.Add((track, power));
            }
        }

        return fighters;
    }

    /// <summary>
    /// The raid points of defense the fighters' stocks add to the observed capacity on <paramref name="day"/> (#3852): each one's
    /// power at its stored level then less at its observed level, <see cref="DefenseCapacityPolicy.DefensePointsPerDps"/> a DPS,
    /// as the sparring ring and the training range shift it (<see cref="ForecastPractice"/>). Negative as a stock decays.
    /// </summary>
    private static double DefenseShift(List<(SkillTrack Track, EquatableArray<double> Power)> fighters, double day) =>
        fighters.Sum(f => DefenseCapacityPolicy.DefensePointsPerDps * (f.Power[f.Track.StoredLevelOn(day)] - f.Power[f.Track.StoredLevel]));

    /// <summary>
    /// False when the schedule says no raid comes and the assessed disease schedule no incident over the horizon (the tended harm is
    /// then 0); true otherwise, unread included.
    /// </summary>
    private bool HarmExpected =>
        !(HarmLedger.RaidsPerDay(context.Harm, horizonDays) == 0 && context.Harm.Illness.Assessed && context.Harm.Illness.IncidentsPerDay(horizonDays) == 0);

    /// <summary>
    /// Capability-days the colony loses to raid injuries and illness over the horizon at the tending the trajectory gives them (#3635,
    /// #3829): for each span of days, the harm term a day (raids a day × each raid's loss through the harm model, bleeding, healing and
    /// infection over the horizon as the ledger counts a raid, plus the illness term's loss, <see cref="IllnessForecast"/>) at the
    /// span's tend quality. That quality is the best doctor's native MedicalTendQuality at the stored Medicine level its stock reaches
    /// (#3633, #3634) through the game's tend formula with the best medicine on hand then (<see cref="TendQuality"/>); TendUtility.DoTend
    /// tends a disease by the same quality as a wound. The raids meet the defense capacity the fighters' Melee and Shooting stocks
    /// give then (<see cref="DefenseShift"/>, #3852), so a level they gain or lose moves the raid loss through the points ratio
    /// the way the sparring ring's does. With no doctor the wounds and illnesses go untended. Zero with neither to
    /// expect. Null, with findings and every skill and medicine key unpriced, when the raid harm model, a doctor's curve or a
    /// medicine's potency cannot say; throws a RequiredFactException when the illness term is unassessed.
    /// </summary>
    private double? TendedHarm(ColonyTrajectory trajectory, SortedSet<string> findings, SortedSet<string> unpriced)
    {
        if (!HarmExpected)
        {
            return 0;
        }

        double? Unpriced()
        {
            unpriced.UnionWith(trajectory.Skills.Select(t => t.Key));
            unpriced.UnionWith(trajectory.MedicineKinds.Select(ProjectGraph.MedicineKey));
            return null;
        }

        if (Doctors(trajectory, findings, unpriced) is not { } doctors)
        {
            return Unpriced();
        }

        var fighters = Fighters(trajectory);
        var total = 0.0;
        foreach (var (from, to, kind) in MedicineSpans(trajectory))
        {
            var cuts = doctors.SelectMany(d => d.Track.Dates).Concat(fighters.SelectMany(f => f.Track.Dates)).Select(d => d.Day)
                .Where(d => d > from && d < to).Append(from).Append(to).Distinct().Order().ToArray();
            for (var i = 0; i + 1 < cuts.Length; i++)
            {
                if (TendQuality(doctors, cuts[i], kind < 0 ? null : trajectory.MedicineKinds[kind], findings) is not { } quality
                    || HarmLossPerDay(quality, DefenseShift(fighters, cuts[i]), findings) is not { } perDay)
                {
                    return Unpriced();
                }

                total += (cuts[i + 1] - cuts[i]) * perDay;
            }
        }

        double QualityOn(double day)
        {
            return QualityAt(trajectory, doctors, day, findings);
        }

        foreach (var scenario in HarmLedger.RaidContexts(context, horizonDays))
        {
            if (scenario.Harm.Occurrence is not RaidOccurrence.At at) continue;
            var quality = QualityOn(at.Day);
            var shifted = scenario with { Harm = scenario.Harm with { Raid = scenario.Harm.Raid with { Capacity = scenario.Harm.Raid.Capacity + DefenseShift(fighters, at.Day) } } };
            var care = new HarmConsequences.Care(quality >= 0, Math.Max(0, quality), 0, 1);
            var count = HarmLedger.RaidsPerDay(scenario.Harm, horizonDays)!.Value * horizonDays;
            total += count * rows.Values.Sum(p => HarmLedger.RaidOn(shifted, p, care, horizonDays * GameTime.TicksPerDay).Risk);
        }

        var scheduledIllness = context.Harm.Illness with { Incidents = [.. context.Harm.Illness.Incidents.Select(i => i with { Rates = [] })] };
        var incidents = scheduledIllness.IncidentsPerDay(horizonDays) * horizonDays;
        if (incidents > 0)
        {
            foreach (var pawn in rows.Values.OrderBy(p => p.Pawn, StringComparer.Ordinal))
            {
                var care = new HarmConsequences.Care(doctors.Count > 0, 0, 0, 1) { QualityAt = QualityOn };
                var distribution = IllnessForecast.Of(scheduledIllness, pawn, context.CapacityPenalties, care,
                    pawn.OwnedBedRoom?.ImmunityGainSpeedFactor ?? 1, context.Value, horizonDays * GameTime.TicksPerDay, context.Stock);
                findings.UnionWith(distribution.Findings);
                if (!distribution.Known) return Unpriced();
                var (mean, tail) = HarmLedger.Moments(distribution, context.Value.HarmRiskAlpha);
                total += incidents * HarmLedger.Risk(mean, tail, context.Value.HarmRiskWeight);
            }
        }

        findings.Add(RaidFirstCareDelayUnmodelled);
        return total;
    }

    /// <summary>
    /// The tending a wound gets over the horizon (#3804, #3868): the tend quality (<see cref="TendQuality"/>) averaged over the run's
    /// days, each stretch at the doctors' levels at its start and the medicine kind the run holds then (<see cref="MedicineSpans"/>,
    /// which carries the medicine crafted in), each whole day cut apart; untended with no pawn who tends. Null, named, when a doctor's
    /// curve, level or a medicine's numbers are unread.
    /// </summary>
    public ForecastTending? TendingOverHorizon(ColonyTrajectory trajectory, SortedSet<string> findings)
    {
        if (Doctors(trajectory, findings, new SortedSet<string>(StringComparer.Ordinal)) is not { } doctors)
        {
            return null;
        }

        if (doctors.Count == 0)
        {
            return new ForecastTending(false, 0);
        }

        double total = 0, span = 0;
        foreach (var (from, to, kind) in MedicineSpans(trajectory))
        {
            var end = Math.Min(to, horizonDays);
            for (var at = from; at < end;)
            {
                var next = Math.Min(end, Math.Floor(at) + 1);
                if (TendQuality(doctors, at, kind < 0 ? null : trajectory.MedicineKinds[kind], findings) is not { } quality)
                {
                    return null;
                }

                total += (next - at) * quality;
                span += next - at;
                at = next;
            }
        }

        return span > 0 ? new ForecastTending(true, total / span) : TendingOn(trajectory, trajectory.StartDay);
    }

    // The tending on day 0 with the best medicine the run starts with: what the treatments a day are counted at before the run.
    private ForecastTending? TendingNow(ColonyTrajectory trajectory, SortedSet<string> findings)
    {
        if (Doctors(trajectory, findings, new SortedSet<string>(StringComparer.Ordinal)) is not { } doctors)
        {
            return null;
        }

        var kind = Array.FindIndex(trajectory.StartMedicine.ToArray(), held => held > 0);
        return TendQuality(doctors, 0, kind < 0 ? null : trajectory.MedicineKinds[kind], findings) is not { } quality ? null
            : quality < 0 ? new ForecastTending(false, 0) : new ForecastTending(true, quality);
    }

    /// <summary>
    /// The pawns who tend wounds and illnesses: each with a native tend quality curve and its stock of the curve's skill. A pawn without a
    /// curve whose skill is disabled cannot tend; any other is named (<see cref="MedicineSkillValueUnassessed"/>) and its stock
    /// unpriced. Null when no pawn qualifies while one is unassessed, or the pawn census is unknown.
    /// </summary>
    private List<(SkillTrack Track, SkillStatCurve Curve)>? Doctors(ColonyTrajectory trajectory, SortedSet<string> findings, SortedSet<string> unpriced)
    {
        var curves = doctors.Select(p => (Pawn: p, Curve: p.SkillStatCurves.FirstOrDefault(c => c.Stat == TendQualityStat))).ToArray();
        var skill = curves.Select(c => c.Curve?.Skill).FirstOrDefault(s => s is not null);
        var found = new List<(SkillTrack, SkillStatCurve)>();
        var unassessed = false;
        foreach (var (pawn, curve) in curves.OrderBy(c => c.Pawn.Pawn, StringComparer.Ordinal))
        {
            var track = curve is null ? null : trajectory.Skills.FirstOrDefault(t => t.Pawn == pawn.Pawn && t.Skill == curve.Skill);
            if (curve is not null && track is not null)
            {
                found.Add((track, curve));
                continue;
            }

            // NativeSkillStat gives a pawn whose skill is disabled no curve: it never tends.
            if (curve is null && skill is not null && pawn.Skills.Any(s => s.DefName == skill && s.Learning is { Disabled: true }))
            {
                continue;
            }

            findings.Add($"{MedicineSkillValueUnassessed}:{pawn.Pawn}");
            unpriced.UnionWith(trajectory.Skills.Where(t => t.Pawn == pawn.Pawn && (skill is null || t.Skill == skill)).Select(t => t.Key));
            unassessed = true;
        }

        return found.Count == 0 && unassessed ? null : found;
    }

    /// <summary>
    /// The tend quality on <paramref name="day"/> (<c>TendUtility.CalculateBaseTendQuality</c>): the best doctor's MedicalTendQuality at
    /// its stored level then × the medicine's potency, clamped to its quality cap; without medicine the native no-medicine potency and
    /// cap. The bed's tend quality offset and self-tending are not modelled. -1 when no pawn tends; null, named, when a level or a
    /// medicine's numbers are unread. The mirrored rule: docs/developers/contracts/kept-constants.md (raid health consequences).
    /// </summary>
    private double? TendQuality(List<(SkillTrack Track, SkillStatCurve Curve)> tenders, double day, Resource? kind, SortedSet<string> findings)
    {
        if (tenders.Count == 0)
        {
            return -1;
        }

        var best = double.NegativeInfinity;
        foreach (var (track, curve) in tenders)
        {
            var stored = track.StoredLevelOn(day);
            if (curve.Levels.FirstOrDefault(l => l.StoredLevel == stored) is not { } at)
            {
                findings.Add($"{MedicineSkillValueUnassessed}:{track.Pawn}:level_{stored}");
                return null;
            }

            best = Math.Max(best, at.Value);
        }

        double factor, max;
        if (kind is { } held)
        {
            if (!medicine.MedicalPotency.TryGetValue(held, out factor) | !medicine.MedicalQualityMax.TryGetValue(held, out max))
            {
                findings.Add("medicine_potency_unknown:" + held.Value);
                return null;
            }
        }
        else
        {
            (factor, max) = (medicine.NoMedicinePotency, medicine.NoMedicineQualityMax);
        }

        return Math.Clamp(best * factor, 0, max);
    }

    /// <summary>
    /// The spans of the run with the medicine kind a doctor takes then (the index into <see cref="ColonyTrajectory.MedicineKinds"/>,
    /// -1 for none): between events the stock falls at a constant rate and is used best kind first, so each kind runs out inside an
    /// interval at the share of the fall it held.
    /// </summary>
    private static IEnumerable<(double From, double To, int Kind)> MedicineSpans(ColonyTrajectory trajectory)
    {
        var held = trajectory.StartMedicine.ToArray();
        var at = trajectory.StartDay;
        foreach (var e in trajectory.Events)
        {
            var next = e.Medicine.Select((count, k) => count + (e.MedicineConsumed.Count > k ? e.MedicineConsumed[k] : 0)).ToArray();
            if (e.Day > at)
            {
                var used = held.Sum() - next.Sum();
                if (!(used > 0))
                {
                    var kind = Array.FindIndex(held, h => h > 0);
                    yield return (at, e.Day, kind >= 0 ? kind : Array.FindIndex(next, h => h > 0));
                }
                else
                {
                    var rate = used / (e.Day - at);
                    var (from, cumulative) = (at, 0.0);
                    for (var k = 0; k < held.Length && from < e.Day; k++)
                    {
                        if (!(held[k] > 0))
                        {
                            continue;
                        }

                        cumulative += held[k];
                        var to = Math.Min(e.Day, at + (cumulative / rate));
                        if (to > from)
                        {
                            yield return (from, to, k);
                        }

                        from = to;
                    }

                    if (from < e.Day)
                    {
                        yield return (from, e.Day, -1);
                    }
                }
            }

            (held, at) = (e.Medicine.ToArray(), e.Day);
        }
    }

    private double QualityAt(ColonyTrajectory trajectory, List<(SkillTrack Track, SkillStatCurve Curve)> doctors, double day, SortedSet<string> findings)
    {
        if (day <= trajectory.StartDay && trajectory.StartDay > 0)
        {
            var history = trajectory.TreatmentHistory ?? throw new RequiredFactException("colony_model_treatment_history_unobserved");
            var earlierDoctors = Doctors(history, findings, new SortedSet<string>(StringComparer.Ordinal))
                ?? throw new RequiredFactException("illness treatment doctor history");
            return QualityAt(history, earlierDoctors, day, findings);
        }
        var treatment = trajectory.Events.FirstOrDefault(e => Math.Abs(e.Day - day) < 1e-9 && e.Kind == ModelEventKind.MedicineTreatment);
        var kind = trajectory.Events.Count == 0 ? Array.FindIndex(trajectory.StartMedicine.ToArray(), held => held > 0) : -1;
        if (treatment is not null)
            kind = Array.FindIndex(treatment.Medicine.Select((held, k) => held + (treatment.MedicineConsumed.Count > k ? treatment.MedicineConsumed[k] : 0)).ToArray(), held => held > 0);
        else
            foreach (var span in MedicineSpans(trajectory))
                if (span.From <= day && day < span.To) { kind = span.Kind; break; }
        return TendQuality(doctors, day, kind < 0 ? null : trajectory.MedicineKinds[kind], findings)
            ?? throw new RequiredFactException("scheduled incident tend quality");
    }

    /// <summary>Tending at an actual review-relative incident or retend date, before that date's medicine debit.</summary>
    public ForecastTending? TendingOn(ColonyTrajectory trajectory, double day)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        if (Doctors(trajectory, findings, new SortedSet<string>(StringComparer.Ordinal)) is not { } doctors) return null;
        var quality = QualityAt(trajectory, doctors, day, findings);
        return quality < 0 ? new ForecastTending(false, 0) : new ForecastTending(true, quality);
    }

    /// <summary>
    /// The colony's raid and illness harm a day with its wounds and illnesses tended at <paramref name="quality"/> (-1: untended), each
    /// raid and incident priced over the horizon (<see cref="HarmAt"/>); null when the raid part is unknown.
    /// </summary>
    private double? HarmLossPerDay(double quality, double shift, SortedSet<string> findings)
    {
        var term = HarmAt(quality, shift);
        findings.UnionWith(term.Findings);
        return term.Detail is { Illness: { } illness } detail ? (detail.RaidLoss + illness.Loss) / horizonDays : null;
    }

    /// <summary>
    /// The harm term over the horizon at <paramref name="quality"/> (-1: untended), first care at once. Its detail is null when the raid
    /// part is unknown; a known one carries the illness part, and a RequiredFactException is thrown when that is unassessed.
    /// </summary>
    private HarmTerm HarmAt(double quality, double shift = 0)
    {
        if (!tendedHarm.TryGetValue((quality, shift), out var term))
        {
            var support = quality < 0 ? new ForecastSupportConsequences() : new ForecastSupportConsequences { CareServices = TendCare, TendQuality = quality };
            var needs = rows.Keys.ToDictionary(p => p, _ => NoNeeds, StringComparer.Ordinal);
            var outcome = new ForecastCoupledOutcome(new([], 0, 0), true, needs, []) { Support = support };
            var raids = context.Harm.ScheduledRaids.Where(r => r.Timing is IncidentTiming.Mtb).ToArray();
            var continuous = context with
            {
                Harm = context.Harm with
                {
                    ScheduledRaids = raids,
                    Schedule = context.Harm.ScheduledRaids.Count > 0 && raids.Length == 0
                        ? context.Harm.Schedule with { MajorThreatsPerDay = 0, RaidFraction = 0, Raids = [] } : context.Harm.Schedule,
                    Illness = context.Harm.Illness with { Incidents = [.. context.Harm.Illness.Incidents.Select(i => i with { Scheduled = [] })] },
                },
            };
            tendedHarm[(quality, shift)] = term = HarmLedger.Of(outcome, continuous, horizonDays * GameTime.TicksPerDay, tendedCache, rows, null, tendedIllness, shift);
        }

        if (term.Detail is { Illness: null })
        {
            throw new RequiredFactException(context.Harm.Illness.Assessed ? "illness_harm" : "illness_schedule");
        }

        return term;
    }

    /// <summary>
    /// Capability-days a goal is worth if it is completed on day <paramref name="completionDay"/> from now (#3505): its worth at that day
    /// (importance, with the deadline's decay) for the part of the horizon it then stands, as the apparel is worth its harm avoided for
    /// the days it is worn. Zero for a goal not completed inside the horizon. Advancing the completion is what an hour of its work earns.
    /// </summary>
    public double GoalValue(GoalTerminal goal, ModelEventTime completionDay)
    {
        switch (completionDay)
        {
            case ModelEventTime.NotReached: return 0;
            case ModelEventTime.AtDay time:
                if (time.Day >= horizonDays) return 0;
                var gameDay = ((double)context.Needs.Tick.Value / GameTime.TicksPerDay) + time.Day;
                return context.Value.GoalWorthOn(goal.Importance, goal.Deadline, gameDay) * (horizonDays - time.Day) / horizonDays;
        }
        throw new ArgumentOutOfRangeException(nameof(completionDay), completionDay, "undeclared model event time");
    }

    /// <summary>
    /// The continuous MTB medicine use a day (<see cref="ColonyModelPolicy.MedicineTendsPerDay"/>): the treatments the harm model's raid
    /// wounds take at each pawn's worn outfit, and the illness term's tends a day (<see cref="IllnessTerm.TendsPerDay"/>)
    /// at the tending a wound gets at the start of <paramref name="input"/>'s run (the day-0 tend quality of its skill stocks and
    /// medicine on hand): TendUtility.DoTend uses up one medicine a treatment and tends a disease alone. Missing raid harm,
    /// doctor curves, medicine potency or illness facts raise RequiredFactException.
    /// </summary>
    public double TreatmentsPerDay(ColonyModelInput input)
    {
        var raids = HarmLedger.RaidContexts(context, horizonDays).Where(c => c.Harm.Occurrence is RaidOccurrence.Continuous)
            .Sum(c => RaidTreatments(c));
        var illness = context.Harm.Illness with { Incidents = [.. context.Harm.Illness.Incidents.Select(i => i with { Scheduled = [] })] };
        return raids + IllnessTreatments(input, illness).Sum(d => d.Count) / horizonDays;
    }

    /// <summary>Scheduled raid wound batches and disease tends, at the native incident dates and the disease course's actual retend dates.</summary>
    public EquatableArray<ScheduledEffect> ScheduledTreatments()
    {
        var dates = new Dictionary<double, double>();
        foreach (var scenario in HarmLedger.RaidContexts(context, horizonDays))
        {
            if (scenario.Harm.Occurrence is RaidOccurrence.At at)
            {
                var count = RaidTreatments(scenario) * horizonDays;
                if (count > 0) dates[at.Day] = dates.GetValueOrDefault(at.Day) + count;
            }
        }

        var illness = context.Harm.Illness;
        if (!illness.Assessed) throw new RequiredFactException("illness_schedule");
        _ = illness.IncidentsPerDay(horizonDays);
        var slots = new HashSet<double>();
        foreach (var incident in illness.Incidents.Where(i => i.VictimChance > 0 && i.Course.Tendable && i.Course.Tend is not null
            && rows.Values.Any(p => p.Disease.Contracts.Any(c => c.Incident == i.Incident && c.Chance > 0))))
        {
            var scheduled = incident with { Rates = [] };
            foreach (var occurrence in scheduled.Occurrences(horizonDays).Where(o => o.Chance > 0))
            {
                var tend = incident.Course.Tend!;
                if (!(tend.FirstRetendDays > 0) || !(tend.RetendDays > 0)) throw new RequiredFactException("disease_tend_schedule:" + incident.Incident);
                var day = occurrence.Day;
                slots.Add(day);
                day += tend.FirstRetendDays;
                while (day < horizonDays && double.IsFinite(day))
                {
                    slots.Add(day);
                    day += tend.RetendDays;
                }
            }
        }
        foreach (var day in slots) dates.TryAdd(day, 0);
        return EquatableArray.From(dates.OrderBy(d => d.Key).Select(d => new ScheduledEffect
        {
            Day = d.Key, Kind = ModelEventKind.MedicineTreatment, Id = "medicine/tend", Stock = ModelStock.Medicine, Amount = -d.Value,
            MedicineDemand = slots.Contains(d.Key) ? DiseaseTreatmentsAt : null,
        }));
    }

    private double DiseaseTreatmentsAt(ColonyTrajectory trajectory, double day)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var doctors = Doctors(trajectory, findings, new SortedSet<string>(StringComparer.Ordinal))
            ?? throw new RequiredFactException("illness treatment doctor");
        var illness = context.Harm.Illness with { Incidents = [.. context.Harm.Illness.Incidents.Select(i => i with { Rates = [] })] };
        var incidents = illness.IncidentsPerDay(horizonDays) * horizonDays;
        var total = 0.0;
        foreach (var pawn in rows.Values.OrderBy(p => p.Pawn, StringComparer.Ordinal))
        {
            var care = new HarmConsequences.Care(doctors.Count > 0, 0, 0, 1)
            {
                QualityAt = at => QualityAt(trajectory, doctors, at, findings), TreatmentThroughDays = day,
            };
            var distribution = IllnessForecast.Of(illness, pawn, context.CapacityPenalties, care,
                pawn.OwnedBedRoom?.ImmunityGainSpeedFactor ?? 1, context.Value, horizonDays * GameTime.TicksPerDay, context.Stock);
            if (!distribution.Known) throw new RequiredFactException(string.Join(",", distribution.Findings));
            total += incidents * distribution.TreatmentDates.Where(t => Math.Abs(t.Day - day) < 1e-9).Sum(t => t.Count);
        }
        return total;
    }

    private double RaidTreatments(ForecastCoupledContext scenario) => HarmLedger.TreatmentsForRaid(scenario, rows, horizonDays) switch
    {
        ForecastTerm.Assessed assessed => assessed.Value,
        ForecastTerm.Unassessed unread => throw new RequiredFactException(unread.Reason),
        var invalid => throw new ArgumentOutOfRangeException(nameof(scenario), invalid, "Invalid treatment reading."),
    };

    private IReadOnlyList<(double Day, double Count)> IllnessTreatments(ColonyModelInput input, IllnessSchedule illness)
    {
        if (!illness.Assessed) throw new RequiredFactException("illness_schedule");
        var incidents = illness.IncidentsPerDay(horizonDays) * horizonDays;
        if (incidents == 0) return [];
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var held = input.Stocks.Medicine;
        var start = new ColonyTrajectory
        {
            Skills = EquatableArray.From(doctors.SelectMany(p => SkillStock.Of(p, findings)).Select(s => s.Track())),
            MedicineKinds = EquatableArray.From(held.Select(a => a.Resource)),
            StartMedicine = EquatableArray.From(held.Select(a => (double)a.Count)),
        };
        if (TendingNow(start, findings) is not { } tending)
        {
            throw new RequiredFactException("illness treatments per day");
        }

        var dates = new Dictionary<double, double>();
        foreach (var pawn in rows.Values.OrderBy(p => p.Pawn, StringComparer.Ordinal))
        {
            var distribution = IllnessForecast.Of(illness, pawn, context.CapacityPenalties,
                new HarmConsequences.Care(tending.Tended, tending.Quality, 0, 1), pawn.OwnedBedRoom?.ImmunityGainSpeedFactor ?? 1,
                context.Value, horizonDays * GameTime.TicksPerDay, context.Stock);
            if (!distribution.Known) throw new RequiredFactException(string.Join(",", distribution.Findings));
            foreach (var (day, count) in distribution.TreatmentDates)
                dates[day] = dates.GetValueOrDefault(day) + count * incidents;
        }
        return [.. dates.Select(d => (d.Key, d.Value))];
    }

    /// <summary>The colony's capability-days lost to malnutrition; a missing table fails the computation.</summary>
    private double HungerLoss(ColonyTrajectory trajectory, SortedSet<string> findings)
    {
        var tables = context.CapacityPenalties;
        if (!tables.Malnutrition.Valid())
        {
            throw new RequiredFactException("capacity penalty table:" + nameof(tables.Malnutrition));
        }

        var table = tables.Malnutrition;
        var rate = ForecastValue.MalnutritionSeverityPerDay;
        var total = 0.0;
        foreach (var row in rows.Values.OrderBy(r => r.Pawn, StringComparer.Ordinal))
        {
            var (severity, levels) = (row.MalnutritionSeverity, ForecastValue.Levels(row));

            var course = new ForecastValue.Course(table, levels, tables.MovingMinForCapable, severity);
            var clock = 0.0;
            double? died = null;

            // Moves the severity for length days; a rising stretch that reaches the lethal severity stops the walk there.
            void Walk(double length, double perDayRate)
            {
                if (died is not null || length <= 0)
                {
                    return;
                }

                if (perDayRate > 0 && table.Lethality is Lethality.Lethal { Severity: var top })
                {
                    var need = Math.Max(0, (top - course.Severity) / perDayRate);
                    if (need <= length)
                    {
                        course.Advance(need * GameTime.TicksPerDay, perDayRate);
                        course.MarkLethal();
                        died = clock + need;
                        return;
                    }
                }

                course.Advance(length * GameTime.TicksPerDay, perDayRate);
                clock += length;
            }

            foreach (var span in trajectory.Starvation.OrderBy(s => s.Start))
            {
                Walk(span.Start - clock, -rate);
                Walk(span.End - Math.Max(span.Start, clock), rate);
            }

            Walk(horizonDays - clock, -rate);
            course.Report(row.Pawn, findings);
            total += course.LostTicks / GameTime.TicksPerDay;
            if (died is { } day)
            {
                total += Math.Max(0, horizonDays - day) + context.Value.DeathTerminalValue;
            }
        }

        return total;
    }

    /// <summary>
    /// Capability-days the run's apparel avoids in harm (#3622). The garments are walked in the order they are made; each unit goes
    /// to the pawn it helps most (the loss per day of the pawn's outfit then less the loss with the unit in its slot) and is credited
    /// with that gain for the days from its making to the horizon. The pawn wears it from then on, so a later garment in that slot is
    /// credited only with its gain over this one, and a unit no pawn gains from is not worn. A garment the harm model or the gear
    /// options cannot price is named (<see cref="ApparelHarmUnassessed"/>) and credited nothing.
    /// </summary>
    private double ApparelGain(ColonyTrajectory trajectory, SortedSet<string> findings, List<(string Pawn, GearOption Garment, double Day)>? wearers = null)
    {
        var outfits = new SortedDictionary<string, EquatableArray<GearOption>>(StringComparer.Ordinal);
        foreach (var (pawn, worn) in context.Outfits)
        {
            if (rows.ContainsKey(pawn))
            {
                outfits[pawn] = worn;
            }
        }

        var total = 0.0;
        foreach (var made in trajectory.Apparel.OrderBy(m => m.Day).ThenBy(m => m.Node, StringComparer.Ordinal))
        {
            var worn = new SortedDictionary<string, EquatableArray<GearOption>>(outfits, StringComparer.Ordinal);
            var units = new List<(string, GearOption)>();
            if (Wear(made, worn, units) is { } gain)
            {
                total += gain * Math.Max(0, horizonDays - made.Day);
                outfits = worn;
                wearers?.AddRange(units.Select(u => (u.Item1, u.Item2, made.Day)));
            }
            else
            {
                findings.Add(ApparelHarmUnassessed);
            }
        }

        return total;
    }

    /// <summary>
    /// The stuff of the garment each pawn ends up wearing in each slot by the apparel walk (<see cref="ApparelGain"/>), among the units
    /// whose stuff arrives within <paramref name="withinDays"/> (#3623): the clothing runway demands it. A unit's stuff arrives with the
    /// run's last arrival of it at or before the unit is made, now when none (it was in stock). A pawn and slot no such unit goes to is
    /// absent: tier 2 does not model wear, so a like-for-like replacement gains nothing and wins nothing.
    /// </summary>
    public IReadOnlyDictionary<(PawnId Pawn, GearSlot Slot), Resource> ClothingStuff(ColonyTrajectory trajectory, double withinDays)
    {
        var wearers = new List<(string Pawn, GearOption Garment, double Day)>();
        ApparelGain(trajectory, new SortedSet<string>(StringComparer.Ordinal), wearers);
        var chosen = new Dictionary<(PawnId Pawn, GearSlot Slot), Resource>();
        foreach (var (pawn, garment, day) in wearers)
        {
            var arrived = trajectory.Arrivals.Where(a => a.Resource == garment.Stuff && a.Day <= day).Select(a => a.Day).DefaultIfEmpty(0).Max();
            if (!garment.Stuff.IsEmpty && arrived <= withinDays)
            {
                chosen[(new PawnId(pawn), garment.Slot)] = garment.Stuff;
            }
        }

        return chosen;
    }

    /// <summary>
    /// Puts each unit of the garment on the pawn it helps most, in <paramref name="outfits"/>, each wearer and garment added to
    /// <paramref name="wearers"/>; the gain a day, or null while unpriced.
    /// </summary>
    private double? Wear(ApparelMade made, SortedDictionary<string, EquatableArray<GearOption>> outfits, List<(string, GearOption)> wearers)
    {
        var sum = 0.0;
        foreach (var product in made.Products.Where(p => p.Count > 0))
        {
            var option = options.FirstOrDefault(o => o.Definition == product.Resource
                && (string.IsNullOrEmpty(o.Stuff.Value) || made.Ingredients.Any(i => i.Resource == o.Stuff)));
            if (option is null)
            {
                return null;
            }

            for (var unit = 0; unit < product.Count; unit++)
            {
                var (wearer, best) = ((string?)null, 0.0);
                foreach (var (pawn, worn) in outfits)
                {
                    if (DailyLoss(pawn, worn) is not LedgerTerm.Priced(var b) || DailyLoss(pawn, Wearing(worn, option)) is not LedgerTerm.Priced(var a))
                    {
                        return null;
                    }

                    if (b - a > best)
                    {
                        (wearer, best) = (pawn, b - a);
                    }
                }

                if (wearer is null)
                {
                    break;
                }

                sum += best;
                outfits[wearer] = Wearing(outfits[wearer], option);
                wearers.Add((wearer, option));
            }
        }

        return sum;
    }

    private static EquatableArray<GearOption> Wearing(EquatableArray<GearOption> worn, GearOption option) =>
        EquatableArray.From(worn.Where(w => w.Slot != option.Slot).Append(option));

    /// <summary>One pawn's harm in capability-days for a day of raids at the outfit; the horizon is a day so the loss is per day (the cache keys omit it).</summary>
    private LedgerTerm DailyLoss(string pawn, EquatableArray<GearOption> outfit)
    {
        if (losses.TryGetValue((pawn, outfit), out var known))
        {
            return known;
        }

        var outcome = new ForecastCoupledOutcome(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult> { [pawn] = NoNeeds }, []);
        return losses[(pawn, outfit)] = HarmLedger.Of(outcome, context, GameTime.TicksPerDay, harmCache, rows,
            [new ForecastCommitment("apparel", default) { OutfitChanges = [new(pawn, outfit)] }], dailyIllness).Loss;
    }
}
