using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

// The threat schedule of the harm model (#3539 of #3471): how many major threats per day the storyteller the game is running
// sends, and how many of them are raids. The rates are not typed in; the bridge decodes each StorytellerDef from its comps by
// comp class (Catalog/Storyteller.cs, #3859, #3860) and this mirrors what the comp classes do with those props (kept constants:
// "Mirrored game literals: threat schedule"). Default points stay the observed RaidPoints; generator budgets apply their own parms.

/// <summary>What an OnOffCycle comp reads of the colony now (#3860): the days passed, the threat points, StorytellerUtility.GetProgressScore, and whether the colony has no tree connection.</summary>
public readonly record struct CycleNow(double Days, double ThreatPoints, double ProgressScore, bool NoTreeConnection);

/// <summary>
/// A StorytellerCompProperties_OnOffCycle whose incident category is ThreatBig: each cycle of OnDays + OffDays holds a count of
/// incidents drawn from [MinIncidents, MaxIncidents] (numIncidentsRange), each kept with the chance the accept curves give: the
/// acceptFractionByDaysPassedCurve at the days passed times the acceptPercentFactorPerThreatPointsCurve at the threat points times
/// the acceptPercentFactorPerProgressScoreCurve at the progress score (each its points; empty: a factor of 1). A colony with no
/// tree connection swaps in <see cref="OnDaysNoTreeConnectors"/> and <see cref="OffDaysNoTreeConnectors"/> where they are not 0.
/// </summary>
public sealed record ThreatCycleComp(double MinDaysPassed, double OnDays, double OffDays, double MinIncidents, double MaxIncidents, IReadOnlyList<(double Days, double Fraction)> AcceptByDaysPassed)
{
    public IReadOnlyList<(double Points, double Factor)> AcceptByThreatPoints { get; init; } = [];

    public IReadOnlyList<(double Score, double Factor)> AcceptByProgressScore { get; init; } = [];

    public double OnDaysNoTreeConnectors { get; init; }

    public double OffDaysNoTreeConnectors { get; init; }

    /// <summary>The cycle's on and off days for a colony with or without a tree connection (StorytellerComp_OnOffCycle).</summary>
    public (double On, double Off) Days(bool noTreeConnection) => noTreeConnection
        ? (OnDaysNoTreeConnectors != 0 ? OnDaysNoTreeConnectors : OnDays, OffDaysNoTreeConnectors != 0 ? OffDaysNoTreeConnectors : OffDays)
        : (OnDays, OffDays);
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
/// A home-map StorytellerCompProperties_ThreatsGenerator (#3860): from MinDaysPassed, each cycle of OnDays + OffDays holds
/// [MinIncidents, MaxIncidents] (its parms' numIncidentsRange) times the count factor of the difficulty's threatScale, each a threat
/// drawn by weight among <see cref="Incidents"/> (the allowedThreats: RaidEnemy and MechCluster) whose worker would fire; the
/// <see cref="Raid"/> one is a raid.
/// </summary>
public sealed record ThreatGeneratorComp(double MinDaysPassed, double OnDays, double OffDays, double MinIncidents, double MaxIncidents,
    IReadOnlyList<string> Incidents, string Raid)
{
    public int CompIndex { get; init; }
    public GeneratorPoints PointBasis { get; init; } = new GeneratorPoints.Current(1);
    /// <summary>Negative infinity is the identity of max when native has no minimum.</summary>
    public double MinimumPoints { get; init; } = double.NegativeInfinity;

    public double Points(double current) => Math.Max(PointBasis.Evaluate(current), MinimumPoints);
}

public abstract record GeneratorPoints
{
    private GeneratorPoints() { }
    public abstract double Evaluate(double current);
    public sealed record Fixed(double Value) : GeneratorPoints { public override double Evaluate(double current) => Value; }
    public sealed record Current(double Factor) : GeneratorPoints { public override double Evaluate(double current) => current * Factor; }
}

/// <summary>Raids per day at one distinct points budget; harm evaluates each budget before adding its losses.</summary>
public sealed record ScheduledRaid(double Points, double PerDay);

/// <summary>A ThreatBig incident a CategoryIndividualMTBByBiome fires every <see cref="MtbDays"/> (its mtbDaysByBiome row), and whether it is a raid.</summary>
public sealed record ThreatBiomeIncident(string Incident, double MtbDays, bool Raid);

/// <summary>
/// A home-map StorytellerCompProperties_CategoryIndividualMTBByBiome of ThreatBig (#3860): from MinDaysPassed, each ThreatBig
/// incident that targets the home map and has an mtbDaysByBiome row of the colony's biome fires at 1 ÷ its MTB a day when its observed native worker gate allows it
/// (<see cref="ByBiome"/>: the incidents of each biome that has a row).
/// </summary>
public sealed record ThreatBiomeComp(double MinDaysPassed, IReadOnlyDictionary<string, IReadOnlyList<ThreatBiomeIncident>> ByBiome);

/// <summary>
/// One storyteller's decoded threat comps. Unmodelled names the comps that may send a major threat the decoder does not model (a
/// comp class it does not know, or a known class's prop it does not model): each is a named finding and adds no threats.
/// </summary>
public sealed record StorytellerThreats
{
    public IReadOnlyList<ThreatCycleComp> Cycles { get; init; } = [];

    public IReadOnlyList<ThreatMtbComp> Mtbs { get; init; } = [];

    public IReadOnlyList<ThreatGeneratorComp> Generators { get; init; } = [];

    public IReadOnlyList<ThreatBiomeComp> Biomes { get; init; } = [];

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
/// at the read boundary, or when the catalog lacks the storyteller's def or a comp's props are invalid.
/// </summary>
public sealed record ThreatSchedule
{
    public bool Assessed { get; init; }

    public double MajorThreatsPerDay { get; init; }

    public double RaidFraction { get; init; }

    public IReadOnlyList<ScheduledRaid> Raids { get; init; } = [];

    public IReadOnlyList<string> Findings { get; init; } = [];

    // StorytellerCompProperties_OnOffCycle.forceRaidEnemyBeforeDaysPassed is 20 in the Core Cassandra and Phoebe comps:
    // StorytellerComp_OnOffCycle.GenerateIncident fires IncidentDefOf.RaidEnemy for every incident before that day.
    internal const double RaidsForcedBeforeDays = 20;

    // After it the incident is drawn by StorytellerComp.IncidentChanceFinal, whose base weight is IncidentDef.baseChance
    // (RaidEnemy 7.4 in Core) against the other ThreatBig incidents that can fire; about half are raids (forecast-value.md, Harm model).
    internal const double RaidFractionAfterForced = 0.5;

    // Mathf.RoundToInt(days * 60): IncidentCycleUtility.DaysToIntervals, 60 intervals of 1000 ticks a day.
    private const double IntervalsPerDay = 60;

    // ThreatsGenerator.ThreatScaleToCountFactorCurve (a private static field of a static class): the difficulty's threatScale to the
    // factor on its parms' numIncidentsRange.
    private static readonly (double Scale, double Factor)[] ThreatScaleToCountFactor =
        [(0, 0.1), (0.3, 0.5), (0.6, 0.8), (1, 1), (1.55, 1.1), (2.2, 1.2), (10, 2)];

    /// <summary>
    /// The schedule of the storyteller the colony runs (<paramref name="threat"/>'s Storyteller) at its days passed
    /// (GenDate.DaysPassedSinceSettleFloat), its cycles at its raid points (StorytellerUtility.DefaultThreatPointsNow), progress score and tree
    /// connection, its generators at the difficulty's threat scale and its biome comps at the colony's biome.
    /// </summary>
    public static ThreatSchedule Of(ThreatScheduleTable table, ThreatFacts threat)
    {
        var days = threat.DaysPassed;
        if (!double.IsFinite(days) || days < 0)
        {
            return Unread("days_passed_invalid");
        }

        if (!table.Storytellers.TryGetValue(threat.Storyteller, out var threats))
        {
            return Unread("storyteller_unknown:" + threat.Storyteller);
        }

        var now = new CycleNow(days, threat.RaidPoints, threat.ProgressScore, threat.NoTreeConnection);
        var flat = days < RaidsForcedBeforeDays ? 1.0 : RaidFractionAfterForced;
        double perDay = 0, raids = 0, defaultRaids = 0;
        var generatedRaids = new List<ScheduledRaid>();
        foreach (var cycle in threats.Cycles)
        {
            var (on, off) = cycle.Days(now.NoTreeConnection);
            var length = Intervals(on) + Intervals(off);
            if (on < 0 || off < 0 || length <= 0 || cycle.MinIncidents < 0 || cycle.MaxIncidents < cycle.MinIncidents)
            {
                return Unread("threat_comp_invalid:OnOffCycle");
            }

            if (days < cycle.MinDaysPassed)
            {
                continue;
            }

            var rate = CyclePerDay(cycle, now);
            perDay += rate;
            raids += rate * flat;
            defaultRaids += rate * flat;
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
                var rate = comp.ThreatBigShare / mtb;
                perDay += rate;
                raids += rate * flat;
                defaultRaids += rate * flat;
            }
        }

        foreach (var comp in threats.Generators)
        {
            var length = Intervals(comp.OnDays) + Intervals(comp.OffDays);
            if (comp.OnDays < 0 || comp.OffDays < 0 || length <= 0 || comp.MinIncidents < 0 || comp.MaxIncidents < comp.MinIncidents
                || !double.IsFinite(comp.Points(threat.RaidPoints)) || comp.Points(threat.RaidPoints) < 0 || !double.IsFinite(threat.GeneratorStartDays))
            {
                return Unread("threat_comp_invalid:ThreatsGenerator");
            }

            var start = threat.GeneratorStartDays;
            if (days < comp.MinDaysPassed || days < start)
            {
                continue;
            }

            // ThreatsGenerator.MakeThreat: a threat is drawn by weight among the incidents whose worker would fire, and none when no
            // incident can (TryRandomElementByWeight over a weight sum of 0).
            double total = 0, raid = 0;
            foreach (var name in comp.Incidents)
            {
                var read = threat.Generator.FirstOrDefault(r => r.CompIndex == comp.CompIndex && r.Incident == name) ?? throw new RequiredFactException("threat_generator_incident_unobserved:" + comp.CompIndex + ":" + name);
                if (read.CanFire && read.Weight > 0)
                {
                    total += read.Weight;
                    raid += name == comp.Raid ? read.Weight : 0;
                }
            }

            if (total > 0)
            {
                var factor = Curve(ThreatScaleToCountFactor, threat.ThreatScale);
                var rate = (comp.MinIncidents + comp.MaxIncidents) / 2 * factor * IntervalsPerDay / length;
                perDay += rate;
                raids += rate * raid / total;
                generatedRaids.Add(new ScheduledRaid(comp.Points(threat.RaidPoints), rate * raid / total));
            }
        }

        // CategoryIndividualMTBByBiome tests the native worker at its unforced default category parms after the MTB roll.
        foreach (var comp in threats.Biomes)
        {
            if (days < comp.MinDaysPassed || !comp.ByBiome.TryGetValue(threat.Biome, out var incidents))
            {
                continue;
            }

            foreach (var incident in incidents)
            {
                if (!(incident.MtbDays > 0))
                {
                    return Unread("threat_comp_invalid:CategoryIndividualMTBByBiome");
                }

                var read = threat.BiomeIncidents.FirstOrDefault(r => r.Incident == incident.Incident)
                    ?? throw new RequiredFactException("threat_biome_incident_unobserved:" + incident.Incident);
                if (read.CanFire)
                {
                    perDay += 1 / incident.MtbDays;
                    raids += incident.Raid ? 1 / incident.MtbDays : 0;
                    defaultRaids += incident.Raid ? 1 / incident.MtbDays : 0;
                }
            }
        }

        List<string> findings = [];
        if (threats.Unmodelled.Count > 0)
        {
            findings.Add("threat_comp_unmodelled:" + string.Join(",", threats.Unmodelled));
        }

        return new ThreatSchedule
        {
            Assessed = true,
            MajorThreatsPerDay = perDay,
            RaidFraction = perDay > 0 ? raids / perDay : flat,
            Raids = generatedRaids.Append(new ScheduledRaid(threat.RaidPoints, defaultRaids))
                .Where(r => r.PerDay > 0).GroupBy(r => r.Points).Select(g => new ScheduledRaid(g.Key, g.Sum(r => r.PerDay))).ToArray(),
            Findings = findings,
        };
    }

    /// <summary>
    /// An OnOffCycle comp's incidents a day at <paramref name="now"/>: the mean count of its range, kept with the accept curves'
    /// chance, over the cycle's intervals. The hit count is GenMath.RoundRandom(Rand.Range(min, max)) and the accepted count
    /// RoundRandom(hits × fraction): unbiased, so the means multiply. The trader cycles (#3869) and the disease cycles (#3860) read it too.
    /// </summary>
    internal static double CyclePerDay(ThreatCycleComp cycle, CycleNow now)
    {
        var (on, off) = cycle.Days(now.NoTreeConnection);
        var length = Intervals(on) + Intervals(off);
        if (length <= 0)
        {
            return 0;
        }

        var accept = Curve(cycle.AcceptByDaysPassed, now.Days) * Curve(cycle.AcceptByThreatPoints, now.ThreatPoints) * Curve(cycle.AcceptByProgressScore, now.ProgressScore);
        return (cycle.MinIncidents + cycle.MaxIncidents) / 2 * accept * IntervalsPerDay / length;
    }

    private static ThreatSchedule Unread(string finding) => new() { Findings = [finding] };

    private static double Intervals(double days) => Math.Round(days * IntervalsPerDay, MidpointRounding.ToEven);

    // SimpleCurve.Evaluate: linear between points, the end values beyond them; no curve is 1. The trader draws (#3909) read it too.
    internal static double Curve(IReadOnlyList<(double X, double Y)> points, double x)
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
