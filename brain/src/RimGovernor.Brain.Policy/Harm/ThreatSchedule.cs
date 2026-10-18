namespace RimGovernor.Brain.Policy;

// The threat schedule of the harm model (#3539 of #3471): how many major threats per day the storyteller the game is running
// sends, and how many of them are raids. The rates are not typed in; the bridge decodes each StorytellerDef's threat-producing
// comps (Catalog/Storyteller.cs) and this mirrors what the comp classes do with those props (kept constants:
// "Mirrored game literals: threat schedule"). Raid points are not modelled here: they stay the observed RaidPoints.

/// <summary>
/// A StorytellerCompProperties_OnOffCycle whose incident category is ThreatBig: each cycle of OnDays + OffDays holds a count of
/// incidents drawn from [MinIncidents, MaxIncidents] (numIncidentsRange), each kept with the chance the accept curve gives at
/// the days passed. AcceptByDaysPassed is the acceptFractionByDaysPassedCurve's points (empty: always accepted).
/// </summary>
public sealed record ThreatCycleComp(double MinDaysPassed, double OnDays, double OffDays, double MinIncidents, double MaxIncidents, IReadOnlyList<(double Days, double Fraction)> AcceptByDaysPassed);

/// <summary>A StorytellerCompProperties_RandomMain: an incident every MtbDays on average, ThreatBig with the share its category weights give.</summary>
public sealed record ThreatRandomComp(double MinDaysPassed, double MtbDays, double ThreatBigShare);

/// <summary>
/// One storyteller's decoded threat comps. Unmodelled names the comp classes (or comp shapes) that may send a major threat the
/// decoder does not model; any makes the storyteller's rate unknown, never a lower one.
/// </summary>
public sealed record StorytellerThreats
{
    public IReadOnlyList<ThreatCycleComp> Cycles { get; init; } = [];

    public IReadOnlyList<ThreatRandomComp> Randoms { get; init; } = [];

    public IReadOnlyList<string> Unmodelled { get; init; } = [];
}

/// <summary>Every storyteller def's threat comps by defName; empty without a catalog (every storyteller is then unknown).</summary>
public sealed record ThreatScheduleTable
{
    public IReadOnlyDictionary<string, StorytellerThreats> Storytellers { get; init; } = new Dictionary<string, StorytellerThreats>();
}

/// <summary>The expected major threats per day and the share of them that are raids; either is unknown with a finding saying why.</summary>
public sealed record ThreatSchedule
{
    public Fact<double> MajorThreatsPerDay { get; init; }

    public Fact<double> RaidFraction { get; init; }

    public IReadOnlyList<string> Findings { get; init; } = [];

    // StorytellerCompProperties_OnOffCycle.forceRaidEnemyBeforeDaysPassed is 20 in the Core Cassandra and Phoebe comps:
    // StorytellerComp_OnOffCycle.GenerateIncident fires IncidentDefOf.RaidEnemy for every incident before that day.
    internal const double RaidsForcedBeforeDays = 20;

    // After it the incident is drawn by StorytellerComp.IncidentChanceFinal, whose base weight is IncidentDef.baseChance
    // (RaidEnemy 7.4 in Core) against the other ThreatBig incidents that can fire; about half are raids (forecast-value.md, Harm model).
    internal const double RaidFractionAfterForced = 0.5;

    // Mathf.RoundToInt(days * 60): IncidentCycleUtility.DaysToIntervals, 60 intervals of 1000 ticks a day.
    private const double IntervalsPerDay = 60;

    /// <summary>The schedule at <paramref name="daysPassed"/> (GenDate.DaysPassedFloat) of <paramref name="storyteller"/>'s def name.</summary>
    public static ThreatSchedule Of(ThreatScheduleTable table, Fact<string> storyteller, Fact<double> daysPassed)
    {
        if (!storyteller.TryGetValue(out var name))
        {
            return Unassessed("storyteller_unobserved");
        }

        if (!daysPassed.TryGetValue(out var days) || !double.IsFinite(days) || days < 0)
        {
            return Unassessed("days_passed_unobserved");
        }

        if (!table.Storytellers.TryGetValue(name, out var threats))
        {
            return Unassessed("storyteller_unknown:" + name);
        }

        if (threats.Unmodelled.Count > 0)
        {
            return Unassessed("threat_comp_unmodelled:" + string.Join(",", threats.Unmodelled));
        }

        var perDay = 0.0;
        foreach (var cycle in threats.Cycles)
        {
            var length = Intervals(cycle.OnDays) + Intervals(cycle.OffDays);
            if (cycle.OnDays < 0 || cycle.OffDays < 0 || length <= 0 || cycle.MinIncidents < 0 || cycle.MaxIncidents < cycle.MinIncidents)
            {
                return Unassessed("threat_comp_invalid:OnOffCycle");
            }

            // The hit count is GenMath.RoundRandom(Rand.Range(min, max)) and the accepted count RoundRandom(hits * fraction): unbiased, so the means multiply.
            if (days >= cycle.MinDaysPassed)
            {
                perDay += (cycle.MinIncidents + cycle.MaxIncidents) / 2 * Curve(cycle.AcceptByDaysPassed, days) * IntervalsPerDay / length;
            }
        }

        foreach (var random in threats.Randoms)
        {
            if (!(random.MtbDays > 0) || random.ThreatBigShare < 0 || random.ThreatBigShare > 1)
            {
                return Unassessed("threat_comp_invalid:RandomMain");
            }

            // Rand.MTBEventOccurs(mtbDays, 60000, 1000) fires at a rate of 1 / mtbDays a day.
            if (days >= random.MinDaysPassed)
            {
                perDay += random.ThreatBigShare / random.MtbDays;
            }
        }

        return new ThreatSchedule
        {
            MajorThreatsPerDay = Known(perDay),
            RaidFraction = Known(days < RaidsForcedBeforeDays ? 1.0 : RaidFractionAfterForced),
        };
    }

    private static ThreatSchedule Unassessed(string finding) => new() { Findings = [finding] };

    private static double Intervals(double days) => Math.Round(days * IntervalsPerDay, MidpointRounding.ToEven);

    // SimpleCurve.Evaluate: linear between points, the end values beyond them; no curve is 1.
    private static double Curve(IReadOnlyList<(double Days, double Fraction)> points, double x)
    {
        if (points.Count == 0)
        {
            return 1;
        }

        if (x <= points[0].Days)
        {
            return points[0].Fraction;
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (x < points[i].Days)
            {
                var (x0, y0) = points[i - 1];
                var (x1, y1) = points[i];
                return y0 + ((y1 - y0) * (x - x0) / (x1 - x0));
            }
        }

        return points[^1].Fraction;
    }
}
