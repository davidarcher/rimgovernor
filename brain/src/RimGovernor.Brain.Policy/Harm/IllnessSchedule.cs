using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

// The illness rate of the harm model (#3824 of #3650): how many DiseaseHuman incidents a day the storyteller sends, and which
// disease each is. The bridge decodes the storytellers' StorytellerCompProperties_Disease comps, the biomes' disease MTB and
// commonalities and every DiseaseHuman IncidentDef with its hediff's course (Catalog/Disease.cs); the colony's facts are the
// native read (DiseaseFacts). This mirrors StorytellerComp_Disease.MakeIntervalIncidents, StorytellerComp.UsableIncidentsInCategory
// with IncidentWorker.CanFireNow, and IncidentWorker_DiseaseHuman.ActualVictims (kept constants: "Mirrored game literals: illness").

/// <summary>How a comp other than StorytellerComp_Disease draws a DiseaseHuman incident (#3860), as its class does.</summary>
public enum DiseaseDrawRule
{
    /// <summary>StorytellerComp_OnOffCycle by category: UsableIncidentsInCategory by IncidentChanceFinal weight.</summary>
    OnOffCycle,

    /// <summary>StorytellerComp_RandomMain: a category by weight (ThreatBig forced past maxThreatBigIntervalDays), then TrySelectRandomIncident.</summary>
    RandomMain,

    /// <summary>StorytellerComp_CategoryMTB: its category, TrySelectRandomIncident.</summary>
    CategoryMtb,

    /// <summary>StorytellerComp_SingleMTB: its one incident.</summary>
    SingleMtb,
}

/// <summary>
/// A storyteller comp other than StorytellerComp_Disease that draws DiseaseHuman incidents (#3860), from <see cref="MinDaysPassed"/>
/// days after settling (an on-off cycle sends raids before forceRaidEnemyBeforeDaysPassed, which the decoder folds into it). Its
/// rate: <see cref="Cycle"/>'s incidents a day for an OnOffCycle, 1 ÷ <see cref="MtbDays"/> (over <see cref="MtbFactorByDaysPassed"/>)
/// for MTB comps. RandomMain uses its comp-indexed native joint interval process; a SingleMTB comp fires <see cref="Incident"/>.
/// <see cref="MinPopulationIntentFactor"/> is IncidentChanceFactor_PopulationIntent's floor.
/// </summary>
public sealed record DiseaseDrawComp(DiseaseDrawRule Rule, double MinDaysPassed)
{
    public int CompIndex { get; init; }
    public string Incident { get; init; } = "";

    public ThreatCycleComp? Cycle { get; init; }

    public double MtbDays { get; init; }

    public IReadOnlyList<(double Days, double Factor)> MtbFactorByDaysPassed { get; init; } = [];


    public double MinPopulationIntentFactor { get; init; }
}

/// <summary>
/// A StorytellerCompProperties_CategoryIndividualMTBByBiome of DiseaseHuman (#3860): from <see cref="MinDaysPassed"/>, each
/// DiseaseHuman incident with an mtbDaysByBiome row of the colony's biome fires every MtbDays (<see cref="ByBiome"/>).
/// </summary>
public sealed record DiseaseBiomeComp(double MinDaysPassed, IReadOnlyDictionary<string, IReadOnlyList<(string Incident, double MtbDays)>> ByBiome);

/// <summary>
/// One storyteller's DiseaseHuman comps: each StorytellerComp_Disease comp's minDaysPassed, the other comps that draw DiseaseHuman
/// incidents (<see cref="Draws"/>, <see cref="BiomeComps"/>, #3860). Unmodelled names a comp that may send DiseaseHuman the decoder
/// does not model; it is a named finding of the schedule and sends no incidents (#3859).
/// </summary>
public sealed record StorytellerDiseases
{
    public IReadOnlyList<double> MinDaysPassed { get; init; } = [];

    public IReadOnlyList<DiseaseDrawComp> Draws { get; init; } = [];

    public IReadOnlyList<DiseaseBiomeComp> BiomeComps { get; init; } = [];

    public IReadOnlyList<string> Unmodelled { get; init; } = [];
}

/// <summary>A biome's diseaseMtbDays and the commonality of each disease incident in it (<c>BiomeDef.CommonalityOfDisease</c>).</summary>
public sealed record DiseaseBiome(double MtbDays, IReadOnlyDictionary<string, double> Commonality);

/// <summary>
/// The TendDuration comp of a disease: <c>severityPerDayTended</c> (times the tend quality while tended), the days from the first
/// tend to the second (baseTendDurationHours: a tend is allowed again once the overlap is all that is left) and between later ones
/// (baseTendDurationHours + tendOverlapHours, <c>TendTicksFull</c>), infinite for a permanent tend, and the total tend quality at
/// which the hediff is removed (<c>disappearsAtTotalTendQuality</c>; negative is never).
/// </summary>
public sealed record TendCourse(double SeverityPerDayTended, double FirstRetendDays, double RetendDays, double CureAtTotalQuality);

/// <summary>
/// The course of a disease hediff: its penalty table (stages, lethal and initial severity, the Immunizable and Disappears comps),
/// the def's severity clamp, the SeverityPerDay comp's rate range (severityPerDay + severityPerDayRange; 0 without the comp), the
/// Immunizable comp's not-immune random factor range, whether it is tendable and its TendDuration comp (null without), the
/// incident's affected part defs (<see cref="DiseasePart"/>; empty for a disease of the whole body, whose lethal severity always
/// kills), and whether reaching the max severity destroys the part (HediffComp_DestroyOrgan). Unmodelled names what the course
/// does not model and makes it unassessed; Unpriced names effects left out of the price.
/// </summary>
public sealed record DiseaseCourse
{
    public HediffPenaltyTable Table { get; init; } = new();

    public double MinSeverity { get; init; }

    public double MaxSeverity { get; init; } = double.PositiveInfinity;

    public double PerDayMin { get; init; }

    public double PerDayMax { get; init; }

    public double NotImmuneFactorMin { get; init; } = 1;

    public double NotImmuneFactorMax { get; init; } = 1;

    public bool Tendable { get; init; }

    public TendCourse? Tend { get; init; }

    public IReadOnlyList<DiseasePart> Parts { get; init; } = [];

    public bool DestroysOrgan { get; init; }

    public IReadOnlyList<string> Unmodelled { get; init; } = [];

    public IReadOnlyList<string> Unpriced { get; init; } = [];

    /// <summary>Reports a valid table and finite, ordered numbers.</summary>
    public bool Valid() =>
        Table.Valid() && double.IsFinite(MinSeverity) && !double.IsNaN(MaxSeverity) && MinSeverity <= MaxSeverity
        && double.IsFinite(PerDayMin) && double.IsFinite(PerDayMax) && PerDayMin <= PerDayMax
        && double.IsFinite(NotImmuneFactorMin) && double.IsFinite(NotImmuneFactorMax) && NotImmuneFactorMin <= NotImmuneFactorMax
        && Parts.All(p => double.IsFinite(p.MinForCapable) && p.Recipes.Values.All(double.IsFinite))
        && (Tend is null || double.IsFinite(Tend.SeverityPerDayTended) && Tend.FirstRetendDays > 0 && Tend.RetendDays > 0 && !double.IsNaN(Tend.CureAtTotalQuality));
}

/// <summary>
/// One part def a disease incident affects (<c>diseasePartsToAffect</c>, #3830): whether its lethal severity kills on it
/// (<c>onlyLifeThreateningTo</c>; every part without the list), the part tag whose efficiency it gives and the capacity that tag
/// feeds (BreathingSource: Breathing, BloodPumpingSource: BloodPumping, BloodFiltrationKidney, BloodFiltrationLiver and
/// BloodFiltrationSource: BloodFiltration; both empty when the def carries another tag or more than one, or when a Human part
/// outside the affected defs carries the tag), whether the capacity is lethalFlesh (the pawn dies once it is not CapableOf it: its
/// level at or below minForCapable), its minForCapable, and the
/// tier (<c>PartTier</c>) of each install recipe that replaces it (Recipe_InstallNaturalBodyPart or
/// Recipe_InstallArtificialBodyPart naming it in appliedOnFixedBodyParts).
/// </summary>
public sealed record DiseasePart
{
    public string Part { get; init; } = "";

    public bool Threatened { get; init; }

    public string Tag { get; init; } = "";

    public string Capacity { get; init; } = "";

    public bool Lethal { get; init; }

    public double MinForCapable { get; init; }

    public IReadOnlyDictionary<string, double> Recipes { get; init; } = new Dictionary<string, double>();
}

/// <summary>
/// One DiseaseHuman IncidentDef: its hediff (<c>diseaseIncident</c>), the victim fraction range and cap, the earliestDay and
/// minPopulation gates, the biome lists, whether its target tags allow the player's home map, and its hediff's course.
/// </summary>
public sealed record DiseaseIncidentRules
{
    public string Incident { get; init; } = "";

    public double FractionMin { get; init; }

    public double FractionMax { get; init; }

    public int MaxVictims { get; init; }

    public int EarliestDay { get; init; }

    public int MinPopulation { get; init; }

    /// <summary>The incident's populationEffect is not None, and the offset IncidentChanceFactor_PopulationIntent adds to the intent for it (#3860).</summary>
    public bool ChangesPopulation { get; init; }

    public double IntentOffset { get; init; }

    public IReadOnlyList<string> AllowedBiomes { get; init; } = [];

    public IReadOnlyList<string> DisallowedBiomes { get; init; } = [];

    public bool OnHomeMap { get; init; }

    public DiseaseCourse Course { get; init; } = new();
}

/// <summary>The catalog's disease tables (#3824); empty without a catalog, which leaves the illness unassessed.</summary>
public sealed record DiseaseTable
{
    public IReadOnlyDictionary<string, StorytellerDiseases> Storytellers { get; init; } = new Dictionary<string, StorytellerDiseases>();

    public IReadOnlyDictionary<string, DiseaseBiome> Biomes { get; init; } = new Dictionary<string, DiseaseBiome>();

    public IReadOnlyDictionary<string, DiseaseIncidentRules> Incidents { get; init; } = new Dictionary<string, DiseaseIncidentRules>();

    /// <summary>StorytellerComp.IncreasesPopChanceByPopIntentCurve (#3860): the chance a category draw takes a population-increasing incident, by population intent.</summary>
    public IReadOnlyList<(double Intent, double Chance)> PopulationChanceByIntent { get; init; } = [];
}

/// <summary>
/// One disease the storyteller can send now: the incidents a day of it, as each comp's rate for it from the day the comp starts
/// (<see cref="Rates"/>: start day relative to now, per day), the chance a potential victim is among the victims drawn
/// (<see cref="IllnessSchedule.VictimChance"/>), and its course.
/// </summary>
public sealed record IllnessIncident(string Incident, IReadOnlyList<(double StartDay, double PerDay)> Rates, double VictimChance, DiseaseCourse Course)
{
    public double ThroughDays { get; init; } = double.PositiveInfinity;
    public IReadOnlyList<IncidentTiming.Scheduled> Scheduled { get; init; } = [];
    public IReadOnlyList<IncidentTiming.Process> Processes { get; init; } = [];
    /// <summary>The incidents of this disease a day over <paramref name="days"/> from now: each comp's rate for the share of the days it runs.</summary>
    public double PerDay(double days)
    {
        return days > 0 ? Occurrences(days).Sum(e => e.Chance) / days : 0;
    }

    public IEnumerable<(double Day, double Chance)> Occurrences(double days)
    {
        if (days > ThroughDays) throw new RequiredFactException("incident_cycle_window_unobserved");
        if (!(days > 0))
        {
            yield break;
        }

        var rate = 0.0;
        foreach (var (start, perDay) in Rates)
        {
            rate += perDay * Math.Clamp(days - Math.Max(0, start), 0, days) / days;
        }

        if (rate > 0) yield return (0, rate * days);
        foreach (var timing in Scheduled)
            foreach (var occurrence in timing.Events(days)) yield return occurrence;
        foreach (var timing in Processes)
            foreach (var occurrence in timing.Events(days)) yield return occurrence;
    }
}

/// <summary>
/// The colony's disease incidents: each DiseaseHuman comp's rate for each usable incident (StorytellerComp_Disease at 1 ÷ (biome MTB
/// × the difficulty's interval factor) a day, drawing by the biome's commonality; the other comps by their own draw, #3860) from
/// the day it starts (its minDaysPassed less the days since settling), with elapsed-day gates evaluated at that start. Unassessed (with
/// <see cref="Findings"/> naming why) when a fact it rests on is unread (<see cref="Unread"/>) or the table does not model the
/// colony's storyteller, biome, incident or course. An assessed schedule's Findings name the storyteller comps it does not model
/// (<c>disease_comp_unmodelled:&lt;comps&gt;</c>), which send no incidents. Future comp starts retain the observed non-time inputs,
/// named <c>disease_future_state_unprojected</c>.
/// </summary>
public sealed record IllnessSchedule
{
    private static readonly string[] FutureStateFindings = ["disease_future_state_unprojected"];
    public double ThroughDays { get; init; } = double.PositiveInfinity;
    public bool Assessed { get; init; }

    /// <summary>Whether the schedule is unassessed for a colony fact left unread (the disease facts, an incident's victims or
    /// a rate input), which the harm term raises as a required fact (#3835); otherwise the table does not model the colony.</summary>
    public bool Unread { get; init; }

    public IReadOnlyList<IllnessIncident> Incidents { get; init; } = [];

    public IReadOnlyList<string> Findings { get; init; } = [];

    /// <summary>The incidents a day over <paramref name="days"/> from now: the sum of every usable disease's rate.</summary>
    public double IncidentsPerDay(double days)
    {
        if (!double.IsFinite(days) || days < 0 || days > ThroughDays)
            throw new RequiredFactException("incident_cycle_window_unobserved");
        return Incidents.Sum(i => i.PerDay(days));
    }

    /// <summary>The share of the incidents over <paramref name="days"/> that are <paramref name="incident"/>.</summary>
    public double Share(IllnessIncident incident, double days)
    {
        var total = IncidentsPerDay(days);
        return total > 0 ? incident.PerDay(days) / total : 0;
    }

    /// <summary>The schedule the table and the colony's disease facts give; see the type summary.</summary>
    public static IllnessSchedule Of(DiseaseTable table, ThreatFacts threat)
    {
        if (threat is null) throw new RequiredFactException("disease observation");
        var facts = threat.Disease;
        if (facts is null)
        {
            throw new RequiredFactException("disease observation");
        }

        if (!table.Storytellers.TryGetValue(facts.Storyteller, out var teller))
        {
            return Unassessed("disease_storyteller_unknown:" + facts.Storyteller);
        }

        // A comp the table does not model is named and sends no incidents (#3859); the modelled comps are still the rate.
        string[] unmodelled = teller.Unmodelled.Count > 0 ? ["disease_comp_unmodelled:" + string.Join(",", teller.Unmodelled)] : [];
        if (!table.Biomes.TryGetValue(facts.Biome, out var biome))
        {
            return Unassessed("disease_biome_unknown:" + facts.Biome);
        }

        if (!double.IsFinite(facts.IntervalFactor) || !double.IsFinite(facts.DaysSinceSettle) || !(biome.MtbDays > 0))
        {
            return Unassessed("disease_rate_invalid") with { Unread = true };
        }

        // StorytellerComp_Disease yields nothing with random diseases off or at the difficulty's cutoff; Storyteller.MakeIncidentsForInterval
        // runs a comp only once the days since settling pass its minDaysPassed.
        var mtb = biome.MtbDays * facts.IntervalFactor;
        var diseaseOn = facts.RandomDiseases && facts.IntervalFactor < DiseaseIntervalFactorCutoff && mtb > 0;

        var rows = new Dictionary<string, DiseaseIncidentRead>(StringComparer.Ordinal);
        foreach (var read in facts.Incidents)
        {
            if (!table.Incidents.ContainsKey(read.Incident))
            {
                return Unassessed("disease_incident_unknown:" + read.Incident);
            }

            rows[read.Incident] = read;
        }

        // IncidentWorker.CanFireNow and CanFireNowSub: the home map, the biome lists, minPopulation, the game-state
        // gates the native read reports, and a potential victim; TryRandomElementByWeight never picks a weight of 0.

        var open = new List<(DiseaseIncidentRules Rules, DiseaseIncidentRead Read)>();
        foreach (var rules in table.Incidents.Values.OrderBy(r => r.Incident, StringComparer.Ordinal))
        {
            if (!rows.TryGetValue(rules.Incident, out var read))
            {
                return Unassessed("disease_victims_unobserved:" + rules.Incident) with { Unread = true };
            }

            if (!rules.OnHomeMap || rules.AllowedBiomes.Count > 0 && !rules.AllowedBiomes.Contains(facts.Biome)
                || rules.DisallowedBiomes.Contains(facts.Biome) || rules.MinPopulation > 0 && facts.FreeColonists < rules.MinPopulation
                || read.Blocked.Count > 0 || read.Potential <= 0)
            {
                continue;
            }

            open.Add((rules, read));
        }

        // Only elapsed time can be projected from this frame. The other gates, victims and rate inputs below are conditional
        // on the observed colony; name that bound when a comp starts in the future rather than inventing its future state.
        var futureStart = teller.MinDaysPassed.Any(d => d > facts.DaysSinceSettle)
            || teller.Draws.Any(c => c.MinDaysPassed > facts.DaysSinceSettle)
            || teller.BiomeComps.Any(c => c.MinDaysPassed > facts.DaysSinceSettle);
        List<(DiseaseIncidentRules Rules, DiseaseIncidentRead Read)> OpenAt(double start) =>
            [.. open.Where(o => o.Rules.EarliestDay <= Math.Floor(start))];
        var rates = new Dictionary<string, List<(double StartDay, double PerDay)>>(StringComparer.Ordinal);
        var scheduled = new Dictionary<string, List<IncidentTiming.Scheduled>>(StringComparer.Ordinal);
        var processes = new Dictionary<string, List<IncidentTiming.Process>>(StringComparer.Ordinal);
        var process = teller.Draws.Any(c => c.Rule == DiseaseDrawRule.RandomMain)
            ? new RandomMainProcess(threat.Trade, table.PopulationChanceByIntent) : null;
        void Send(string incident, double minDaysPassed, double perDay)
        {
            if (!(perDay > 0))
            {
                return;
            }

            if (!rates.TryGetValue(incident, out var list))
            {
                rates[incident] = list = [];
            }

            list.Add((minDaysPassed - facts.DaysSinceSettle, perDay));
        }

        // StorytellerComp_Disease: an event at 1 / mtb a day, the incident drawn by the biome's commonality.
        if (diseaseOn)
        {
            foreach (var min in teller.MinDaysPassed)
            {
                var weighted = OpenAt(Math.Max(facts.DaysSinceSettle, min))
                    .Select(o => (o.Rules.Incident, Weight: biome.Commonality.GetValueOrDefault(o.Rules.Incident))).Where(o => o.Weight > 0).ToList();
                var all = weighted.Sum(o => o.Weight);
                foreach (var (incident, weight) in weighted)
                {
                    Send(incident, min, 1 / mtb * weight / all);
                }
            }
        }

        foreach (var comp in teller.Draws)
        {
            if (comp.Rule == DiseaseDrawRule.RandomMain)
            {
                if (!process!.Contains(comp.CompIndex)) throw new RequiredFactException("random_main_comp_unobserved:" + comp.CompIndex);
                var drawn = threat.Trade.Categories.Where(c => c.CompIndex == comp.CompIndex)
                    .SelectMany(c => c.Outcomes).SelectMany(o => o.Incidents).Select(i => i.Incident).ToHashSet(StringComparer.Ordinal);
                foreach (var (rules, _) in open.Where(o => drawn.Contains(o.Rules.Incident)))
                {
                    if (!rates.ContainsKey(rules.Incident)) rates[rules.Incident] = [];
                    if (!processes.TryGetValue(rules.Incident, out var events)) processes[rules.Incident] = events = [];
                    events.Add(new IncidentTiming.Process(process, comp.CompIndex, rules.Incident));
                }
                continue;
            }
            var start = Math.Max(facts.DaysSinceSettle, comp.MinDaysPassed);
            if (comp.Rule == DiseaseDrawRule.OnOffCycle)
            {
                var cycle = comp.Cycle ?? throw new DomainException("disease cycle comp without a cycle");
                var times = IncidentTiming.RequireCycle(threat.Cycles, cycle.CompIndex);
                foreach (var at in times.Days.Distinct().Where(d => d + facts.DaysSinceSettle >= comp.MinDaysPassed))
                {
                    DrawComp(comp, table, OpenAt(facts.DaysSinceSettle + at), start, threat.Trade.PopulationIntent, (name, _, chance) =>
                    {
                        if (!rates.ContainsKey(name)) rates[name] = [];
                        if (!scheduled.TryGetValue(name, out var events)) scheduled[name] = events = [];
                        events.Add(new IncidentTiming.Scheduled(times, chance) { From = at, Until = Math.BitIncrement(at) });
                    });
                }
                continue;
            }
            DrawComp(comp, table, OpenAt(start), start, threat.Trade.PopulationIntent, Send);
        }

        foreach (var comp in teller.BiomeComps)
        {
            // StorytellerComp_CategoryIndividualMTBByBiome: each incident of the category with a row of the biome at 1 / its MTB a day,
            // when its worker can fire.
            if (comp.ByBiome.TryGetValue(facts.Biome, out var individual))
            {
                var usableAtStart = OpenAt(Math.Max(facts.DaysSinceSettle, comp.MinDaysPassed));
                foreach (var (incident, days) in individual)
                {
                    if (!(days > 0))
                    {
                        return Unassessed("disease_comp_invalid:CategoryIndividualMTBByBiome");
                    }

                    if (usableAtStart.Any(o => o.Rules.Incident == incident))
                    {
                        Send(incident, comp.MinDaysPassed, 1 / days);
                    }
                }
            }
        }

        var usable = new List<IllnessIncident>();
        foreach (var (rules, read) in open)
        {
            if (!rates.TryGetValue(rules.Incident, out var perDay))
            {
                continue;
            }

            if (rules.Course.Unmodelled.Count > 0)
            {
                return Unassessed("disease_course_unmodelled:" + rules.Course.Table.Hediff + ":" + string.Join(",", rules.Course.Unmodelled));
            }

            if (!rules.Course.Valid())
            {
                return Unassessed("disease_course_unknown:" + rules.Incident);
            }

            usable.Add(new IllnessIncident(rules.Incident, perDay,
                VictimChance(facts.Candidates, read.Potential, rules.FractionMin, rules.FractionMax, rules.MaxVictims), rules.Course)
            {
                Scheduled = scheduled.GetValueOrDefault(rules.Incident) ?? [],
                Processes = processes.GetValueOrDefault(rules.Incident) ?? [],
                ThroughDays = Math.Min(process is null ? double.PositiveInfinity : threat.Trade.Clock!.ThroughDays, teller.Draws.Where(c => c.Rule == DiseaseDrawRule.OnOffCycle)
                    .Select(c => IncidentTiming.RequireCycle(threat!.Cycles, c.Cycle!.CompIndex).ThroughDays).DefaultIfEmpty(double.PositiveInfinity).Min()),
            });
        }

        return new IllnessSchedule
        {
            Assessed = true, Findings = unmodelled.Concat(process is null ? [] : RandomMainProcess.Conditions)
                .Concat(futureStart ? FutureStateFindings : []).ToArray(), Incidents = usable,
            ThroughDays = Math.Min(process is null ? double.PositiveInfinity : threat.Trade.Clock!.ThroughDays, teller.Draws.Where(c => c.Rule == DiseaseDrawRule.OnOffCycle)
                .Select(c => IncidentTiming.RequireCycle(threat.Cycles, c.Cycle!.CompIndex).ThroughDays)
                .DefaultIfEmpty(double.PositiveInfinity).Min()),
        };
    }

    // A comp other than StorytellerComp_Disease: its rate and the chance each of its events is each open incident (#3860).
    private static void DrawComp(DiseaseDrawComp comp, DiseaseTable table, List<(DiseaseIncidentRules Rules, DiseaseIncidentRead Read)> open,
        double startDay, double populationIntent, Action<string, double, double> send)
    {
        // IncidentChanceFinal: the read weight (without the population-intent factor) times that factor.
        var usable = open.Select(o => (o.Rules.Incident, o.Rules.ChangesPopulation,
                Weight: Math.Max(0, o.Read.Weight * TradeArrivalRules.IntentFactor(o.Rules.ChangesPopulation, o.Rules.IntentOffset, populationIntent, comp.MinPopulationIntentFactor))))
            .Where(u => u.Weight > 0).ToList();
        switch (comp.Rule)
        {
            case DiseaseDrawRule.SingleMtb:
                if (open.Any(o => o.Rules.Incident == comp.Incident))
                {
                    send(comp.Incident, comp.MinDaysPassed, 1 / comp.MtbDays);
                }

                return;
            case DiseaseDrawRule.OnOffCycle:
                if (comp.Incident.Length > 0)
                {
                    // A cycle of one incident fires it.
                    if (open.Any(o => o.Rules.Incident == comp.Incident))
                    {
                        send(comp.Incident, comp.MinDaysPassed, 1);
                    }

                    return;
                }

                var total = usable.Sum(u => u.Weight);
                foreach (var u in usable)
                {
                    send(u.Incident, comp.MinDaysPassed, u.Weight / total);
                }

                return;
            case DiseaseDrawRule.CategoryMtb:
                var factor = ThreatSchedule.Curve(comp.MtbFactorByDaysPassed, startDay);
                foreach (var (incident, chance) in TradeArrivalRules.SelectRandomIncident([.. usable], populationIntent, table.PopulationChanceByIntent))
                {
                    send(incident, comp.MinDaysPassed, chance / (comp.MtbDays * factor));
                }

                return;
            case DiseaseDrawRule.RandomMain:
                throw new DomainException("RandomMain must use the observed joint interval process");
        }
    }

    // Difficulty.DiseaseIntervalFactorCutoff.
    internal const double DiseaseIntervalFactorCutoff = 100;

    /// <summary>
    /// The chance a potential victim is drawn (IncidentWorker_DiseaseHuman.ActualVictims): the count is uniform over the integers
    /// from RoundToInt(candidates × min) to RoundToInt(candidates × max) (IntRange.RandomInRange), clamped to [1, maxVictims], and
    /// that many are taken from the <paramref name="potential"/> victims in random order.
    /// </summary>
    public static double VictimChance(int candidates, int potential, double fractionMin, double fractionMax, int maxVictims)
    {
        if (potential <= 0)
        {
            return 0;
        }

        var lo = (int)Math.Round(candidates * fractionMin, MidpointRounding.ToEven);
        var hi = Math.Max(lo, (int)Math.Round(candidates * fractionMax, MidpointRounding.ToEven));
        var taken = 0.0;
        for (var k = lo; k <= hi; k++)
        {
            taken += Math.Min(Math.Clamp(k, 1, Math.Max(1, maxVictims)), potential);
        }

        return taken / (hi - lo + 1) / potential;
    }

    private static IllnessSchedule Unassessed(string finding) => new() { Findings = [finding] };
}
