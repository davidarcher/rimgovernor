using System.Collections.Concurrent;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>The catalog's decoded harm tables (#3546): the raid factions, where a hit lands and the race's own armor. Empty without a catalog.</summary>
public sealed record HarmCatalog
{
    public RaidTable Raid { get; init; } = new();
    public BodyCoverage Coverage { get; init; } = new();
    public NaturalArmor Natural { get; init; } = new();
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

    /// <summary>The inputs the review observed: the schedule at the storyteller and days passed, the composition of the observed raid points.</summary>
    internal static HarmInputs Of(RoundsFacts facts) => new()
    {
        Schedule = ThreatSchedule.Of(facts.ThreatTable, facts.Storyteller, facts.DaysPassed),
        Composition = facts.RaidPoints.TryGetValue(out var points) && facts.HostileFactions.TryGetValue(out var factions)
            ? RaidCompositionRules.Of(points, [.. factions], facts.HarmCatalog.Raid)
            : new RaidComposition { Findings = [facts.RaidPoints.IsKnown ? "raid_hostile_factions_unobserved" : "raid_points_unobserved"] },
        RaidPoints = facts.RaidPoints,
        DefenseCapacity = facts.DefenseCapacity,
        Coverage = facts.HarmCatalog.Coverage,
        Natural = facts.HarmCatalog.Natural,
    };
}

/// <summary>
/// The per-raid numbers behind a known harm term (#3546), reported by the decision's telemetry. The probabilities are expected
/// pawns per raid summed over the colony; <see cref="Cvar"/> is the colony's summed CVaR of the per-pawn loss, in capability-days.
/// </summary>
public sealed record HarmDetail(double RaidsPerDay, double ExposureLo, double ExposureHi, double DownProbability, double DeathProbability, double Cvar)
{
    /// <summary>Expected pawn deaths per day: the death floor's measure.</summary>
    public double DeathsPerDay => RaidsPerDay * DeathProbability;
}

/// <summary>The harm term of one outcome: the loss in capability-days (unknown with <see cref="Findings"/> saying why) and the detail behind it.</summary>
internal sealed record HarmTerm(Fact<double> Loss, HarmDetail? Detail, IReadOnlyList<string> Findings);

/// <summary>
/// The harm term (harm model, #3471): raids per day × horizon days × Σ over pawns of the risk-transformed loss of one raid,
/// where the pawn's raid is <see cref="HarmConsequences.Of"/> over the exposure range at points ÷ defense capacity
/// (<see cref="RaidExposure"/>) and the armor response of its outfit to the raid's weapon bins (<see cref="ArmorResponseRules.OfOutfit"/>).
/// The risk transform is (1 − w) × E + w × CVaRα with the policy's <see cref="ForecastValuePolicy.HarmRiskWeight"/> and
/// <see cref="ForecastValuePolicy.HarmRiskAlpha"/>. Unknown, with the specific finding, when any input is. A raid is counted
/// at its whole-horizon loss wherever it falls in the horizon, and the horizon's days of raids are the schedule's expectation
/// at the observed days passed.
/// </summary>
internal static class HarmLedger
{
    internal const string ThreatScheduleUnknown = "threat_schedule_unknown";
    internal const string CompositionUnknown = "raid_composition_unknown";

    /// <summary>The threats the harm term leaves out in v1 (non-raid threats, adaptation feedback, fire), reported with every known term.</summary>
    private static readonly string[] Unmodelled = ["other_threats_unassessed", "adaptation_feedback_unassessed", "fire_unassessed"];

    /// <summary>The harm of <paramref name="outcome"/> wearing the worn outfits changed by <paramref name="commitments"/>.</summary>
    internal static HarmTerm Of(ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon,
        ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> cache, IReadOnlyDictionary<string, NeedsForecastPawn> pawns, IEnumerable<ForecastCommitment>? commitments)
    {
        var inputs = context.Harm;
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var complete = true;
        var days = horizon / GameTime.TicksPerDay;
        var raidsPerDay = 0.0;
        if (inputs.Schedule.MajorThreatsPerDay.TryGetValue(out var threats) && inputs.Schedule.RaidFraction.TryGetValue(out var raidShare))
        {
            raidsPerDay = threats * raidShare;
            if (raidsPerDay == 0)
            {
                // A schedule with no raids to expect has no harm to price, whatever else is unobserved.
                return new(Known(0d), new HarmDetail(0, 0, 0, 0, 0, 0), []);
            }
        }
        else
        {
            complete = false;
            findings.Add(ThreatScheduleUnknown);
            findings.UnionWith(inputs.Schedule.Findings);
        }

        var bins = inputs.Composition.Bins.ToArray();
        if (!inputs.Composition.Pawns.IsKnown || bins.Length == 0)
        {
            complete = false;
            findings.Add(CompositionUnknown);
            findings.UnionWith(inputs.Composition.Findings);
        }

        var ratio = double.NaN;
        if (!inputs.RaidPoints.TryGetValue(out var points))
        {
            findings.Add("raid_points_unobserved");
        }
        else if (!inputs.DefenseCapacity.TryGetValue(out var defense) || !(defense > 0))
        {
            findings.Add("defense_capacity_unobserved");
        }
        else
        {
            ratio = points / defense;
        }

        complete &= !double.IsNaN(ratio);
        var ordered = outcome.Needs.Keys.ToArray();
        Array.Sort(ordered, StringComparer.Ordinal);
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

            var role = RoleOf(wearing);
            var reading = RaidExposure.HitsPerColonist(inputs.Exposure, ratio, role);
            findings.UnionWith(reading.Findings);
            if (!reading.Hits.TryGetValue(out var hits))
            {
                complete = false;
                continue;
            }

            var outfit = OutfitOf(pawn, wearing, commitments);
            var key = (pawn, role, outfit, outcome.Support, hits);
            if (!cache.TryGetValue(key, out var distribution))
            {
                var response = ArmorResponseRules.OfOutfit(outfit, inputs.Natural, inputs.Coverage, bins);
                cache[key] = distribution = HarmConsequences.Of(hits, bins, response, row, context.CapacityPenalties, outcome.Support, context.Value, horizon);
            }

            findings.UnionWith(distribution.Findings);
            if (!distribution.Known)
            {
                complete = false;
                continue;
            }

            var (mean, tail) = Moments(distribution, context.Value.HarmRiskAlpha);
            risked += Risk(mean, tail, context.Value.HarmRiskWeight);
            lo = Math.Min(lo, hits.Lo);
            hi = Math.Max(hi, hits.Hi);
            down += distribution.DownProbability;
            death += distribution.DeathProbability;
            cvar += tail;
        }

        if (!complete)
        {
            return new(default, null, [.. findings]);
        }

        findings.UnionWith(Unmodelled);
        return new(Known(raidsPerDay * days * risked), new HarmDetail(raidsPerDay, lo, hi, down, death, cvar), [.. findings]);
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

    /// <summary>The garments and weapon the pawn wears under <paramref name="commitments"/>: the last outfit change for it, else the worn items.</summary>
    internal static EquatableArray<GearOption> OutfitOf(string pawn, EquatableArray<GearOption> worn, IEnumerable<ForecastCommitment>? commitments)
    {
        var outfit = worn;
        if (commitments is null)
        {
            return outfit;
        }

        foreach (var commitment in commitments)
        {
            foreach (var change in commitment.OutfitChanges)
            {
                if (change.Pawn == pawn)
                {
                    outfit = change.Items;
                }
            }
        }

        return outfit;
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
