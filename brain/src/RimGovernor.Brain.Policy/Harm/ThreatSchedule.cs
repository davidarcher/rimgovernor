namespace RimGovernor.Brain.Policy;

// The threat schedule of the harm model (#3539 of #3471): how many major threats per day the storyteller the game is running
// sends, and how many of them are raids. The rates are not typed in; the bridge decodes each StorytellerDef from its comps by
// comp class (Catalog/Storyteller.cs, #3859) and this mirrors what the comp classes do with those props (kept constants:
// "Mirrored game literals: threat schedule"). Raid points are not modelled here: they stay the observed RaidPoints.

/// <summary>
/// A StorytellerCompProperties_OnOffCycle whose incident category is ThreatBig: each cycle of OnDays + OffDays holds a count of
/// incidents drawn from [MinIncidents, MaxIncidents] (numIncidentsRange), each kept with the chance the accept curves give: the
/// acceptFractionByDaysPassedCurve at the days passed times the acceptPercentFactorPerThreatPointsCurve at the threat points
/// (each its points; empty: a factor of 1).
/// </summary>
public sealed record ThreatCycleComp(double MinDaysPassed, double OnDays, double OffDays, double MinIncidents, double MaxIncidents, IReadOnlyList<(double Days, double Fraction)> AcceptByDaysPassed)
{
    public IReadOnlyList<(double Points, double Factor)> AcceptByThreatPoints { get; init; } = [];
}

/// <summary>
/// A comp that fires an incident every MtbDays (times its mtbDaysFactorByDaysPassedCurve at the days passed; empty: 1) on average,
/// ThreatBig with ThreatBigShare: a StorytellerCompProperties_RandomMain (the share its category weights give), a CategoryMTB of
/// ThreatBig or a SingleMTB of a ThreatBig incident (share 1).
/// </summary>
public sealed record ThreatMtbComp(double MinDaysPassed, double MtbDays, double ThreatBigShare)
{
    public IReadOnlyList<(double Days, double Factor)> MtbFactorByDaysPassed { get; init; } = [];
}

/// <summary>
/// One storyteller's decoded threat comps. Unmodelled names the comps that may send a major threat the decoder does not model (a
/// comp class it does not know, or a known class's prop it does not model): each is a named finding and adds no threats.
/// </summary>
public sealed record StorytellerThreats
{
    public IReadOnlyList<ThreatCycleComp> Cycles { get; init; } = [];

    public IReadOnlyList<ThreatMtbComp> Mtbs { get; init; } = [];

    public IReadOnlyList<string> Unmodelled { get; init; } = [];
}

/// <summary>Every storyteller def's threat comps by defName; empty without a catalog (every storyteller is then unread).</summary>
public sealed record ThreatScheduleTable
{
    public IReadOnlyDictionary<string, StorytellerThreats> Storytellers { get; init; } = new Dictionary<string, StorytellerThreats>();
}

/// <summary>
/// The expected major threats per day and the share of them that are raids, from the comps the schedule models; Findings name the
/// comps it does not (<c>threat_comp_unmodelled:&lt;comps&gt;</c>), which add no threats. Not <see cref="Assessed"/>, with a
/// finding naming the gap, when the review did not read the storyteller, the days passed or the raid points
/// (<see cref="Unobserved"/>), or when the catalog lacks the storyteller's def or a comp's props are invalid.
/// </summary>
public sealed record ThreatSchedule
{
    public bool Assessed { get; init; }

    public double MajorThreatsPerDay { get; init; }

    public double RaidFraction { get; init; }

    public IReadOnlyList<string> Findings { get; init; } = [];

    // StorytellerCompProperties_OnOffCycle.forceRaidEnemyBeforeDaysPassed is 20 in the Core Cassandra and Phoebe comps:
    // StorytellerComp_OnOffCycle.GenerateIncident fires IncidentDefOf.RaidEnemy for every incident before that day.
    internal const double RaidsForcedBeforeDays = 20;

    // After it the incident is drawn by StorytellerComp.IncidentChanceFinal, whose base weight is IncidentDef.baseChance
    // (RaidEnemy 7.4 in Core) against the other ThreatBig incidents that can fire; about half are raids (forecast-value.md, Harm model).
    internal const double RaidFractionAfterForced = 0.5;

    // Mathf.RoundToInt(days * 60): IncidentCycleUtility.DaysToIntervals, 60 intervals of 1000 ticks a day.
    private const double IntervalsPerDay = 60;

    /// <summary>
    /// The schedule at <paramref name="daysPassed"/> (GenDate.DaysPassedFloat) of <paramref name="storyteller"/>'s def name, its
    /// threat-points curves at <paramref name="threatPoints"/> (StorytellerUtility.DefaultThreatPointsNow, the observed raid points).
    /// </summary>
    public static ThreatSchedule Of(ThreatScheduleTable table, string storyteller, double daysPassed, double threatPoints)
    {
        var days = daysPassed;
        if (!double.IsFinite(days) || days < 0)
        {
            return Unread("days_passed_invalid");
        }

        if (!table.Storytellers.TryGetValue(storyteller, out var threats))
        {
            return Unread("storyteller_unknown:" + storyteller);
        }

        var perDay = 0.0;
        foreach (var cycle in threats.Cycles)
        {
            var length = Intervals(cycle.OnDays) + Intervals(cycle.OffDays);
            if (cycle.OnDays < 0 || cycle.OffDays < 0 || length <= 0 || cycle.MinIncidents < 0 || cycle.MaxIncidents < cycle.MinIncidents)
            {
                return Unread("threat_comp_invalid:OnOffCycle");
            }

            if (days < cycle.MinDaysPassed)
            {
                continue;
            }

            perDay += CyclePerDay(cycle, days, threatPoints);
        }

        foreach (var comp in threats.Mtbs)
        {
            var mtb = comp.MtbDays * Curve(comp.MtbFactorByDaysPassed, days);
            if (!(mtb > 0) || comp.ThreatBigShare < 0 || comp.ThreatBigShare > 1)
            {
                return Unread("threat_comp_invalid:Mtb");
            }

            // Rand.MTBEventOccurs(mtbDays, 60000, 1000) fires at a rate of 1 / mtbDays a day.
            if (days >= comp.MinDaysPassed)
            {
                perDay += comp.ThreatBigShare / mtb;
            }
        }

        return new ThreatSchedule
        {
            Assessed = true,
            MajorThreatsPerDay = perDay,
            RaidFraction = days < RaidsForcedBeforeDays ? 1.0 : RaidFractionAfterForced,
            Findings = threats.Unmodelled.Count > 0 ? ["threat_comp_unmodelled:" + string.Join(",", threats.Unmodelled)] : [],
        };
    }

    /// <summary>
    /// An OnOffCycle comp's incidents a day at <paramref name="days"/> passed and <paramref name="threatPoints"/>: the mean count of
    /// its range, kept with the accept curves' chance, over the cycle's intervals. The hit count is GenMath.RoundRandom(Rand.Range(min,
    /// max)) and the accepted count RoundRandom(hits × fraction): unbiased, so the means multiply. The trader cycles (#3869) read it too.
    /// </summary>
    internal static double CyclePerDay(ThreatCycleComp cycle, double days, double threatPoints)
    {
        var length = Intervals(cycle.OnDays) + Intervals(cycle.OffDays);
        if (length <= 0)
        {
            return 0;
        }

        var accept = Curve(cycle.AcceptByDaysPassed, days) * Curve(cycle.AcceptByThreatPoints, threatPoints);
        return (cycle.MinIncidents + cycle.MaxIncidents) / 2 * accept * IntervalsPerDay / length;
    }

    /// <summary>The schedule of a review that did not read a fact it needs (the storyteller, the days passed or the raid points), named by <paramref name="finding"/>.</summary>
    public static ThreatSchedule Unobserved(string finding) => Unread(finding);

    private static ThreatSchedule Unread(string finding) => new() { Findings = [finding] };

    private static double Intervals(double days) => Math.Round(days * IntervalsPerDay, MidpointRounding.ToEven);

    // SimpleCurve.Evaluate: linear between points, the end values beyond them; no curve is 1.
    private static double Curve(IReadOnlyList<(double X, double Y)> points, double x)
    {
        if (points.Count == 0)
        {
            return 1;
        }

        if (x <= points[0].X)
        {
            return points[0].Y;
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (x < points[i].X)
            {
                var (x0, y0) = points[i - 1];
                var (x1, y1) = points[i];
                return y0 + ((y1 - y0) * (x - x0) / (x1 - x0));
            }
        }

        return points[^1].Y;
    }
}
