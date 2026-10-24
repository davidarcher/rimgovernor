using System.Collections.Concurrent;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>The catalog's decoded harm tables (#3546): the raid factions, where a hit lands, the race's own armor and the part-loss tables (#3847). Empty without a catalog.</summary>
public sealed record HarmCatalog
{
    public RaidTable Raid { get; init; } = new();
    public BodyCoverage Coverage { get; init; } = new();
    public NaturalArmor Natural { get; init; } = new();
    public RaidPartCatalog Parts { get; init; } = new();
}

/// <summary>
/// What the ledger's harm term reads of the colony's expected raids (#3546 of #3471), assembled once per review: the threat
/// schedule, the raid composition, the exposure table, the observed points and defense capacity, and where a hit lands. The
/// hediff tables are the context's <see cref="ForecastCoupledContext.CapacityPenalties"/>. Every default is unknown, never a number.
/// </summary>
public sealed record HarmInputs
{
    /// <summary>Major threats per day and the share that are raids (<see cref="ThreatSchedule.Of"/>).</summary>
    public ThreatSchedule Schedule { get; init; } = new();

    /// <summary>The expected raid at the observed points (<see cref="RaidCompositionRules.Of"/>).</summary>
    public RaidComposition Composition { get; init; } = new();

    public Fact<double> RaidPoints { get; init; }
    public Fact<double> DefenseCapacity { get; init; }

    /// <summary>The calibrated exposure table; the embedded file unless a test injects one.</summary>
    public ExposureTable Exposure { get; init; } = RaidExposure.Load();

    public BodyCoverage Coverage { get; init; } = new();
    public NaturalArmor Natural { get; init; } = new();

    /// <summary>The damage rules and replacement tiers a raid's part loss reads (#3847); empty by default, which throws where read.</summary>
    public RaidPartCatalog Parts { get; init; } = new();

    /// <summary>The colony's disease incidents (<see cref="IllnessSchedule.Of"/>, #3824); unassessed by default.</summary>
    public IllnessSchedule Illness { get; init; } = new();

    /// <summary>The entities the colony holds or could capture and the containment curves (#3811), which the escape term reads; none by default.</summary>
    public ContainmentForecast Containment { get; init; } = new();

    /// <summary>The inputs the review observed: the schedule at the storyteller and days passed, the composition of the observed raid points, the disease schedule of the observed disease facts, the containment census.</summary>
    internal static HarmInputs Of(RoundsFacts facts) => new()
    {
        Schedule = ThreatSchedule.Of(facts.ThreatTable, facts.Storyteller, facts.DaysPassed, facts.RaidPoints),
        Composition = facts.RaidPoints.TryGetValue(out var points) && facts.HostileFactions.TryGetValue(out var factions)
            ? RaidCompositionRules.Of(points, [.. factions], facts.HarmCatalog.Raid)
            : new RaidComposition { Findings = [facts.RaidPoints.IsKnown ? "raid_hostile_factions_unobserved" : "raid_points_unobserved"] },
        RaidPoints = facts.RaidPoints,
        DefenseCapacity = facts.DefenseCapacity,
        Coverage = facts.HarmCatalog.Coverage,
        Natural = facts.HarmCatalog.Natural,
        Parts = facts.HarmCatalog.Parts,
        Illness = IllnessSchedule.Of(facts.DiseaseTable, facts.Disease),
        Containment = facts.Containment.Forecast,
    };
}

/// <summary>
/// The per-raid numbers behind a known harm term (#3546), reported by the decision's telemetry. The probabilities are expected
/// pawns per raid summed over the colony; <see cref="Cvar"/> is the colony's summed CVaR of the per-pawn loss, in capability-days.
/// </summary>
public sealed record HarmDetail(double RaidsPerDay, double ExposureLo, double ExposureHi, double DownProbability, double DeathProbability, double Cvar)
{
    /// <summary>The raids' part of the term's loss, in capability-days over the horizon.</summary>
    public double RaidLoss { get; init; }

    /// <summary>The illness part (#3824); null when the disease table does not model the colony's illness (the term then holds the raids alone, with an <c>illness_unmodelled</c> finding).</summary>
    public IllnessTerm? Illness { get; init; }

    /// <summary>The escape part (#3811): the held entities' escapes; none without a held entity.</summary>
    public EscapeTerm Escape { get; init; } = new(0, 0, 0);

    /// <summary>Expected pawn deaths per day, from raids, illness and escapes: the death floor's measure.</summary>
    public double DeathsPerDay => RaidsPerDay * DeathProbability + (Illness?.DeathsPerDay ?? 0) + Escape.DeathsPerDay;
}

/// <summary>
/// The escape part of the harm term (#3811): the held entities' escapes a day (Σ 1 / InitiateEscapeMtbDays), their loss over
/// the horizon in capability-days (each escape one fight at the entity's combatPower) and the pawn deaths a day they cause.
/// A fight the exposure model does not cover, or one an unread fact leaves unpriced, throws (<see cref="HarmLedger.FightLoss"/>, #3835).
/// </summary>
public sealed record EscapeTerm(double EscapesPerDay, double Loss, double DeathsPerDay);

/// <summary>The harm term of one outcome: the loss in capability-days (unknown with <see cref="Findings"/> saying why) and the detail behind it.</summary>
internal sealed record HarmTerm(Fact<double> Loss, HarmDetail? Detail, IReadOnlyList<string> Findings);

/// <summary>
/// The harm term (harm model, #3471): raids per day × horizon days × Σ over pawns of the risk-transformed loss of one raid,
/// where the pawn's raid is <see cref="HarmConsequences.Of"/> over the exposure range at points ÷ defense capacity
/// (<see cref="RaidExposure"/>) and the armor response of the outfit it wears when the raid falls (<see cref="OutfitsOf"/>, #3621) to the raid's weapon bins (<see cref="ArmorResponseRules.OfOutfit"/>).
/// The risk transform is (1 − w) × E + w × CVaRα with the policy's <see cref="ForecastValuePolicy.HarmRiskWeight"/> and
/// <see cref="ForecastValuePolicy.HarmRiskAlpha"/>. Unknown, with the specific finding, when any input is. A raid is counted
/// at its whole-horizon loss wherever it falls in the horizon, and the horizon's days of raids are the schedule's expectation
/// at the observed days passed. The illness term (#3824, <see cref="IllnessForecast"/>) adds disease incidents per day × horizon
/// days × Σ over pawns of the same risk transform of one incident's loss; its deaths join the raids' in
/// <see cref="HarmDetail.DeathsPerDay"/>. An illness the disease table does not model leaves the raids' loss alone with
/// <c>illness_unmodelled</c> and the reason, and an unread illness fact throws (#3835); <c>other_threats_unassessed</c> covers
/// the threats other than raids and disease.
/// </summary>
internal static class HarmLedger
{
    internal const string ThreatScheduleUnknown = "threat_schedule_unknown";
    internal const string CompositionUnknown = "raid_composition_unknown";

    /// <summary>The threats the harm term leaves out in v1 (non-raid threats, adaptation feedback, fire), reported with every known term.</summary>
    private static readonly string[] Unmodelled = ["other_threats_unassessed", "adaptation_feedback_unassessed", "fire_unassessed"];

    /// <summary>The harm of <paramref name="outcome"/> wearing the worn outfits changed by <paramref name="commitments"/>, with
    /// <paramref name="defenseShift"/> raid points of defense added to the observed capacity (the sparring ring's trained Melee, #3813).</summary>
    internal static HarmTerm Of(ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon,
        ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> cache, IReadOnlyDictionary<string, NeedsForecastPawn> pawns, IEnumerable<ForecastCommitment>? commitments,
        ConcurrentDictionary<(string, ForecastSupportConsequences?), HarmDistribution>? illnessCache = null, double defenseShift = 0)
    {
        var inputs = context.Harm;
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var complete = true;
        var days = horizon / GameTime.TicksPerDay;
        var raidsPerDay = 0.0;
        var ordered = outcome.Needs.Keys.ToArray();
        Array.Sort(ordered, StringComparer.Ordinal);
        // The schedule's findings: the comps it does not model when assessed (#3859), the unread fact otherwise.
        findings.UnionWith(inputs.Schedule.Findings);
        if (RaidsPerDay(inputs) is { } expected)
        {
            raidsPerDay = expected;
            if (raidsPerDay == 0)
            {
                // A schedule with no raids to expect has no raid harm to price, whatever else is unobserved; illness and escapes are priced alone.
                return WithIllness(new HarmDetail(0, 0, 0, 0, 0, 0), ordered, outcome, context, horizon, cache, pawns, commitments, illnessCache, findings);
            }
        }
        else
        {
            complete = false;
            findings.Add(ThreatScheduleUnknown);
        }

        var bins = BinsOf(inputs) ?? [];
        if (bins.Length == 0)
        {
            complete = false;
            bins = [];
            findings.Add(CompositionUnknown);
            findings.UnionWith(inputs.Composition.Findings);
        }

        var ratio = double.NaN;
        if (PointsRatio(inputs, defenseShift) is { } known)
        {
            ratio = known;
        }
        else
        {
            findings.Add(inputs.RaidPoints.IsKnown ? "defense_capacity_unobserved" : "raid_points_unobserved");
        }

        complete &= !double.IsNaN(ratio);
        if (ordered.Length == 0)
        {
            complete = false;
            findings.Add("harm_unassessed:no_pawns");
        }

        var risked = 0.0;
        double lo = double.PositiveInfinity, hi = 0, down = 0, death = 0, cvar = 0;
        foreach (var pawn in ordered)
        {
            if (!pawns.TryGetValue(pawn, out var row))
            {
                complete = false;
                findings.Add("capacities_unobserved:" + pawn);
                continue;
            }

            if (!context.Outfits.TryGetValue(pawn, out var wearing))
            {
                complete = false;
                findings.Add("outfit_unobserved:" + pawn);
                continue;
            }

            if (!complete)
            {
                continue;
            }

            if (PawnFight(pawn, row, wearing, ratio, bins, outcome, context, horizon, cache, commitments, findings) is not { } fight)
            {
                complete = false;
                continue;
            }

            risked += fight.Risked;
            down += fight.Down;
            death += fight.Death;
            cvar += fight.Cvar;
            lo = Math.Min(lo, fight.Hits.Lo);
            hi = Math.Max(hi, fight.Hits.Hi);
        }

        if (!complete)
        {
            return new(default, null, [.. findings]);
        }

        findings.UnionWith(Unmodelled);
        return WithIllness(new HarmDetail(raidsPerDay, lo, hi, down, death, cvar) { RaidLoss = raidsPerDay * days * risked }, ordered, outcome, context, horizon, cache, pawns, commitments, illnessCache, findings);
    }

    /// <summary>One pawn's part of one fight: its risked loss, its down and death probabilities, its CVaR and the hits it takes.</summary>
    private readonly record struct Fight(double Risked, double Down, double Death, double Cvar, HitRange Hits);

    /// <summary>
    /// <paramref name="pawn"/>'s part of one fight at <paramref name="ratio"/> (points over defense capacity): the hits its exposure role
    /// takes (<see cref="RaidExposure"/>) against each outfit it wears under <paramref name="commitments"/> for its share of the horizon
    /// (#3621), each the risk transform of <see cref="HarmConsequences.Of"/>. Null, with the finding, when the exposure cell, an
    /// outfit change's timing or a distribution is unknown.
    /// </summary>
    private static Fight? PawnFight(string pawn, NeedsForecastPawn row, EquatableArray<GearOption> wearing, double ratio, WeaponBin[] bins, ForecastCoupledOutcome outcome,
        ForecastCoupledContext context, double horizon, ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> cache,
        IEnumerable<ForecastCommitment>? commitments, SortedSet<string> findings)
    {
        var inputs = context.Harm;
        var role = RoleOf(wearing);
        var reading = RaidExposure.HitsPerColonist(inputs.Exposure, ratio, role);
        findings.UnionWith(reading.Findings);
        if (!reading.Hits.TryGetValue(out var hits))
        {
            return null;
        }

        if (OutfitsOf(pawn, wearing, commitments, outcome.OutfitTicks, horizon) is not { } outfits)
        {
            findings.Add("outfit_change_timing_unknown:" + pawn);
            return null;
        }

        double risked = 0, down = 0, death = 0, cvar = 0;
        // Each outfit is worn for its share of the horizon, and the fights in that share meet it (#3621).
        foreach (var (outfit, share) in outfits)
        {
            var key = (pawn, role, outfit, outcome.Support, hits);
            if (!cache.TryGetValue(key, out var distribution))
            {
                var response = ArmorResponseRules.OfOutfit(outfit, inputs.Natural, inputs.Coverage, bins);
                cache[key] = distribution = HarmConsequences.Of(hits, bins, response, PartsOf(inputs, outfit), row, context.CapacityPenalties, outcome.Support, context.Value, horizon);
            }

            findings.UnionWith(distribution.Findings);
            if (!distribution.Known)
            {
                return null;
            }

            var (mean, tail) = Moments(distribution, context.Value.HarmRiskAlpha);
            risked += share * Risk(mean, tail, context.Value.HarmRiskWeight);
            down += share * distribution.DownProbability;
            death += share * distribution.DeathProbability;
            cvar += share * tail;
        }

        return new(risked, down, death, cvar, hits);
    }

    /// <summary>
    /// The colony's risked loss of one fight at <paramref name="points"/> (#3811), in capability-days over the horizon, and the pawn
    /// deaths it causes: Σ over <paramref name="ordered"/> of <see cref="PawnFight"/> at points ÷ defense capacity, the raid's
    /// weapon bins and exposure table standing in for the fight's (the raid-calibration assumption); none with no pawn to fight.
    /// The fight is priced where the exposure model covers it (a calibrated cell, <see cref="RaidExposure"/>): a pawn's fight
    /// outside its coverage, or an unread defense, bins, capacities, outfit, outfit timing or distribution, throws
    /// <see cref="RequiredFactException"/> naming the finding (#3835, replacing #3811's unassessed escape).
    /// </summary>
    internal static (double Loss, double Deaths) FightLoss(double points, string[] ordered, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon,
        ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> cache,
        IReadOnlyDictionary<string, NeedsForecastPawn> pawns, IEnumerable<ForecastCommitment>? commitments, SortedSet<string> findings)
    {
        if (ordered.Length == 0)
        {
            return (0, 0);
        }

        var ratio = RatioOf(context.Harm, points) ?? throw new RequiredFactException("defense_capacity_unobserved");
        var bins = BinsOf(context.Harm) ?? throw new RequiredFactException(CompositionUnknown);
        double loss = 0, deaths = 0;
        foreach (var pawn in ordered)
        {
            if (!pawns.TryGetValue(pawn, out var row) || !context.Outfits.TryGetValue(pawn, out var wearing))
            {
                throw new RequiredFactException((pawns.ContainsKey(pawn) ? "outfit_unobserved:" : "capacities_unobserved:") + pawn);
            }

            var own = new SortedSet<string>(StringComparer.Ordinal);
            var read = PawnFight(pawn, row, wearing, ratio, bins, outcome, context, horizon, cache, commitments, own);
            findings.UnionWith(own);
            if (read is not { } fight)
            {
                throw new RequiredFactException(string.Join(",", own.Where(f => f != RaidExposure.LayoutUnassessed)));
            }

            loss += fight.Risked;
            deaths += fight.Death;
        }

        return (loss, deaths);
    }

    /// <summary>
    /// The escape part (#3811): per entity held on a platform, its escapes a day at the platform's strength now
    /// (<see cref="ForecastContainment.EscapesPerDay"/>) times the days of the horizon and the loss of one fight at its combatPower
    /// (<see cref="FightLoss"/>). No held entity adds nothing. Unread entities or curves, or a fight <see cref="FightLoss"/>
    /// cannot price, raise <see cref="RequiredFactException"/>.
    /// </summary>
    private static EscapeTerm Escapes(string[] ordered, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon,
        ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> cache,
        IReadOnlyDictionary<string, NeedsForecastPawn> pawns, IEnumerable<ForecastCommitment>? commitments, SortedSet<string> findings)
    {
        var held = ForecastContainment.EntitiesOf(context.Harm.Containment).Where(e => e.Platform is not null).OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
        if (held.Length == 0)
        {
            return new(0, 0, 0);
        }

        var curves = ForecastContainment.Curves(context);
        var days = horizon / GameTime.TicksPerDay;
        double perDay = 0, loss = 0, deaths = 0;
        foreach (var entity in held)
        {
            var rate = ForecastContainment.EscapesPerDay(entity, entity.Platform!.Strength, curves);
            if (rate == 0)
            {
                continue;
            }

            var fight = FightLoss(entity.Escape.CombatPower, ordered, outcome, context, horizon, cache, pawns, commitments, findings);
            findings.Add(ForecastContainment.NotModelled[0]);
            perDay += rate;
            loss += rate * days * fight.Loss;
            deaths += rate * fight.Deaths;
        }

        return new(perDay, loss, deaths);
    }

    /// <summary>
    /// The term of a known raid part with the illness (#3824) and escape (#3811) parts added: the loss is the raids' plus the
    /// illness's plus the escapes', priced alike; an illness the table does not model leaves the others alone with its findings.
    /// </summary>
    private static HarmTerm WithIllness(HarmDetail raids, string[] ordered, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon,
        ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> fights,
        IReadOnlyDictionary<string, NeedsForecastPawn> pawns, IEnumerable<ForecastCommitment>? commitments,
        ConcurrentDictionary<(string, ForecastSupportConsequences?), HarmDistribution>? cache, SortedSet<string> findings)
    {
        var illness = IllnessForecast.Term(context.Harm.Illness, ordered, pawns, context.CapacityPenalties, outcome.Support, context.Value, horizon, cache, findings);
        var escape = Escapes(ordered, outcome, context, horizon, fights, pawns, commitments, findings);
        var detail = raids with { Illness = illness, Escape = escape };
        return new(Known(detail.RaidLoss + (illness?.Loss ?? 0) + escape.Loss), detail, [.. findings]);
    }

    /// <summary>
    /// The medicine treatments the colony's raid wounds take a day (#3634): raids per day × Σ over the pawns of the treatments one raid's
    /// wounds take (<see cref="HarmConsequences.Treatments"/>) at the pawn's exposure and worn outfit. Zero when no raid is expected;
    /// unknown when the schedule, composition, ratio, a pawn's outfit or the exposure cell is.
    /// </summary>
    internal static Fact<double> TreatmentsPerDay(ForecastCoupledContext context, IReadOnlyDictionary<string, NeedsForecastPawn> pawns)
    {
        var inputs = context.Harm;
        if (RaidsPerDay(inputs) is not { } raidsPerDay)
        {
            return default;
        }

        if (raidsPerDay == 0)
        {
            return Known(0d);
        }

        if (BinsOf(inputs) is not { } bins || PointsRatio(inputs) is not { } ratio || pawns.Count == 0)
        {
            return default;
        }

        var treatments = 0.0;
        foreach (var (pawn, row) in pawns.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!context.Outfits.TryGetValue(pawn, out var wearing)
                || !RaidExposure.HitsPerColonist(inputs.Exposure, ratio, RoleOf(wearing)).Hits.TryGetValue(out var hits)
                || !HarmConsequences.Treatments(hits, bins, ArmorResponseRules.OfOutfit(wearing, inputs.Natural, inputs.Coverage, bins), row).TryGetValue(out var one))
            {
                return default;
            }

            treatments += one;
        }

        return Known(raidsPerDay * treatments);
    }

    /// <summary>The raids a day the schedule expects: its major threats a day × the raid share; null when the schedule is unread.</summary>
    internal static double? RaidsPerDay(HarmInputs inputs) =>
        inputs.Schedule.Assessed ? inputs.Schedule.MajorThreatsPerDay * inputs.Schedule.RaidFraction : null;

    /// <summary>The raid's points ÷ the colony's defense capacity plus <paramref name="shift"/> points (the sparring ring's trained
    /// Melee, #3813), the exposure table's ratio; null when either is unread or the capacity is not positive.</summary>
    internal static double? PointsRatio(HarmInputs inputs, double shift = 0) => inputs.RaidPoints.TryGetValue(out var points) ? RatioOf(inputs, points, shift) : null;

    /// <summary><paramref name="points"/> ÷ the colony's defense capacity plus <paramref name="shift"/> (a raid's, or an escaped
    /// entity's combatPower, #3811); null when the capacity is unread or not positive.</summary>
    private static double? RatioOf(HarmInputs inputs, double points, double shift = 0) =>
        inputs.DefenseCapacity.TryGetValue(out var defense) && defense > 0 ? points / (defense + shift) : null;

    /// <summary>The raid composition's weapon bins; null when the composition is unread or has none.</summary>
    internal static WeaponBin[]? BinsOf(HarmInputs inputs) =>
        inputs.Composition.Pawns.IsKnown && inputs.Composition.Bins.ToArray() is { Length: > 0 } bins ? bins : null;

    /// <summary>What a raid's part loss reads of <paramref name="outfit"/> and the inputs (#3847).</summary>
    internal static RaidPartInputs PartsOf(HarmInputs inputs, EquatableArray<GearOption> outfit) =>
        new() { Outfit = outfit, Natural = inputs.Natural, Layers = inputs.Coverage.Layers, Catalog = inputs.Parts };

    /// <summary>
    /// One raid's harm on <paramref name="row"/> in its worn outfit given <paramref name="care"/> (#3804, the hospital credit): its
    /// risk-transformed loss (<see cref="Risk"/>), expected patient-days (<see cref="HarmDistribution.PatientDays"/>) and the parts it
    /// destroys that the autopilot would or would not replace (<see cref="HarmDistribution.Replacements"/>, #3847). Throws a
    /// RequiredFactException when the composition, the points ratio, the pawn's outfit, its exposure cell or its harm reads are unread.
    /// </summary>
    internal static (double Risk, double PatientDays, IReadOnlyList<OrganReplacement> Replacements) RaidOn(ForecastCoupledContext context, NeedsForecastPawn row, HarmConsequences.Care care, double horizon)
    {
        var inputs = context.Harm;
        var bins = BinsOf(inputs) ?? throw new RequiredFactException(CompositionUnknown);
        var ratio = PointsRatio(inputs) ?? throw new RequiredFactException("raid_points_ratio");
        if (!context.Outfits.TryGetValue(row.Pawn, out var wearing))
        {
            throw new RequiredFactException("outfit:" + row.Pawn);
        }

        if (!RaidExposure.HitsPerColonist(inputs.Exposure, ratio, RoleOf(wearing)).Hits.TryGetValue(out var hits))
        {
            throw new RequiredFactException("raid_exposure:" + row.Pawn);
        }

        var distribution = HarmConsequences.Of(hits, bins, ArmorResponseRules.OfOutfit(wearing, inputs.Natural, inputs.Coverage, bins), PartsOf(inputs, wearing), row,
            context.CapacityPenalties, care, context.Value, horizon);
        if (!distribution.Known)
        {
            throw new RequiredFactException("raid_harm:" + row.Pawn);
        }

        var (mean, tail) = Moments(distribution, context.Value.HarmRiskAlpha);
        return (Risk(mean, tail, context.Value.HarmRiskWeight), distribution.PatientDays, distribution.Replacements);
    }

    /// <summary>The risk transform of one raid's loss: (1 − w) × mean + w × CVaR.</summary>
    internal static double Risk(double mean, double cvar, double weight) => (1 - weight) * mean + weight * cvar;

    /// <summary>The mean of the distribution and its CVaR at <paramref name="alpha"/>: the mean of the worst (1 − alpha) of the probability mass.</summary>
    internal static (double Mean, double Cvar) Moments(HarmDistribution distribution, double alpha)
    {
        var points = distribution.Points.OrderByDescending(p => p.Loss).ToArray();
        if (points.Length == 0)
        {
            return (0, 0);
        }

        var mean = 0.0;
        foreach (var point in points)
        {
            mean += point.Loss * point.Probability;
        }

        var tail = Math.Clamp(1 - alpha, 0, 1);
        if (tail <= 0)
        {
            return (mean, points[0].Loss);
        }

        var left = tail;
        var sum = 0.0;
        foreach (var point in points)
        {
            var take = Math.Min(left, point.Probability);
            sum += take * point.Loss;
            left -= take;
            if (left <= 0)
            {
                break;
            }
        }

        return (mean, sum / (tail - left));
    }

    /// <summary>
    /// The garments and weapon the pawn wears under <paramref name="commitments"/>, each with the share of the horizon it is worn:
    /// the worn items from the start, then each outfit change from its commitment's <see cref="ChangeTick"/>, in tick order. A whole
    /// outfit replaces the one then worn; an overlay replaces only the items it conflicts with. A change that takes effect at or after
    /// the horizon is never worn. Null when a change's tick is unknown.
    /// </summary>
    internal static List<(EquatableArray<GearOption> Outfit, double Share)>? OutfitsOf(string pawn, EquatableArray<GearOption> worn,
        IEnumerable<ForecastCommitment>? commitments, EquatableArray<ForecastEffectiveTick> ticks, double horizon)
    {
        var changes = new List<(double At, ForecastOutfitChange Change)>();
        foreach (var commitment in commitments ?? [])
        {
            foreach (var change in commitment.OutfitChanges)
            {
                if (change.Pawn != pawn)
                {
                    continue;
                }

                if (ChangeTick(commitment, ticks) is not { } at)
                {
                    return null;
                }

                if (at < horizon || at == 0)
                {
                    changes.Add((at, change));
                }
            }
        }

        var outfits = new List<(EquatableArray<GearOption>, double)>();
        var (outfit, from) = (worn, 0d);
        foreach (var (at, change) in changes.OrderBy(c => c.At).ThenBy(c => c.Change.Overlay))
        {
            if (at > from)
            {
                outfits.Add((outfit, (at - from) / horizon));
                from = at;
            }

            outfit = change.Apply(outfit);
        }

        outfits.Add((outfit, horizon > 0 ? (horizon - from) / horizon : 1));
        return outfits;
    }

    /// <summary>
    /// The tick, from the forecast start, at which <paramref name="commitment"/>'s outfit changes take effect: its row in
    /// <paramref name="ticks"/> for a commitment with activities, a retained bill's completion (#3816), else the start. Null when
    /// that tick is unknown.
    /// </summary>
    internal static double? ChangeTick(ForecastCommitment commitment, EquatableArray<ForecastEffectiveTick> ticks)
    {
        if (commitment.ActivityCompletions.Count == 0)
        {
            return commitment.Outcome is { RetainedCompletion.IsKnown: true } retained ? retained.RetainedCompletion.Value : 0;
        }

        double? at = null;
        foreach (var tick in ticks)
        {
            if (tick.Commitment == commitment.Id)
            {
                at = tick.Tick.IsKnown ? tick.Tick.Value : null;
            }
        }

        return at;
    }

    /// <summary>The exposure role of a pawn from what it wears: a ranged primary weapon is a gunner, a melee one a melee fighter, none a noncombatant.</summary>
    internal static string RoleOf(EquatableArray<GearOption> worn)
    {
        foreach (var item in worn)
        {
            if (item.Slot == GearSlot.Primary)
            {
                return item.Ranged ? ExposureRoles.Gunner : ExposureRoles.Melee;
            }
        }

        return ExposureRoles.Noncombatant;
    }
}
