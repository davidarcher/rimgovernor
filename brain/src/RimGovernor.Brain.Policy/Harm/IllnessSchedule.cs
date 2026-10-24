using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

// The illness rate of the harm model (#3824 of #3650): how many DiseaseHuman incidents a day the storyteller sends, and which
// disease each is. The bridge decodes the storytellers' StorytellerCompProperties_Disease comps, the biomes' disease MTB and
// commonalities and every DiseaseHuman IncidentDef with its hediff's course (Catalog/Disease.cs); the colony's facts are the
// native read (DiseaseFacts). This mirrors StorytellerComp_Disease.MakeIntervalIncidents, StorytellerComp.UsableIncidentsInCategory
// with IncidentWorker.CanFireNow, and IncidentWorker_DiseaseHuman.ActualVictims (kept constants: "Mirrored game literals: illness").

/// <summary>One storyteller's DiseaseHuman comps: each comp's minDaysPassed. Unmodelled names a comp that may send DiseaseHuman the decoder does not model.</summary>
public sealed record StorytellerDiseases
{
    public IReadOnlyList<double> MinDaysPassed { get; init; } = [];

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
/// Immunizable comp's not-immune random factor range, whether it is tendable and its TendDuration comp (null without), the share
/// of the incident's affected parts its lethal severity kills on (<c>onlyLifeThreateningTo</c> by coverage; 1 without the list),
/// and whether reaching the max severity destroys the part (HediffComp_DestroyOrgan). Unmodelled names what the course does
/// not model and makes it unassessed; Unpriced names effects left out of the price.
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

    public double LethalPartShare { get; init; } = 1;

    public bool DestroysOrgan { get; init; }

    public IReadOnlyList<string> Unmodelled { get; init; } = [];

    public IReadOnlyList<string> Unpriced { get; init; } = [];

    /// <summary>Reports a valid table and finite, ordered numbers.</summary>
    public bool Valid() =>
        Table.Valid() && double.IsFinite(MinSeverity) && !double.IsNaN(MaxSeverity) && MinSeverity <= MaxSeverity
        && double.IsFinite(PerDayMin) && double.IsFinite(PerDayMax) && PerDayMin <= PerDayMax
        && double.IsFinite(NotImmuneFactorMin) && double.IsFinite(NotImmuneFactorMax) && NotImmuneFactorMin <= NotImmuneFactorMax
        && LethalPartShare is >= 0 and <= 1
        && (Tend is null || double.IsFinite(Tend.SeverityPerDayTended) && Tend.FirstRetendDays > 0 && Tend.RetendDays > 0 && !double.IsNaN(Tend.CureAtTotalQuality));
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
}

/// <summary>
/// One disease the storyteller can send now: its share of the incidents (its commonality over the usable incidents'), the chance
/// a potential victim is among the victims drawn (<see cref="IllnessSchedule.VictimChance"/>), and its course.
/// </summary>
public sealed record IllnessIncident(string Incident, double Share, double VictimChance, DiseaseCourse Course);

/// <summary>
/// The colony's disease incidents: each DiseaseHuman comp's rate (1 ÷ (biome MTB × the difficulty's interval factor) a day) from
/// the day it starts (its minDaysPassed less the days since settling), and the usable incidents. Unassessed (with
/// <see cref="Findings"/> naming why) when a fact or table it rests on is absent.
/// </summary>
public sealed record IllnessSchedule
{
    public bool Assessed { get; init; }

    public IReadOnlyList<(double StartDay, double PerDay)> Comps { get; init; } = [];

    public IReadOnlyList<IllnessIncident> Incidents { get; init; } = [];

    public IReadOnlyList<string> Findings { get; init; } = [];

    /// <summary>The incidents a day over <paramref name="days"/> from now: each comp's rate for the share of the days it runs.</summary>
    public double IncidentsPerDay(double days)
    {
        if (!(days > 0) || Incidents.Count == 0)
        {
            return 0;
        }

        var rate = 0.0;
        foreach (var (start, perDay) in Comps)
        {
            rate += perDay * Math.Clamp(days - Math.Max(0, start), 0, days) / days;
        }

        return rate;
    }

    /// <summary>The schedule the table and the colony's disease facts give; see the type summary.</summary>
    public static IllnessSchedule Of(DiseaseTable table, DiseaseFacts? facts)
    {
        if (facts is null)
        {
            return Unassessed("disease_facts_unobserved");
        }

        if (!table.Storytellers.TryGetValue(facts.Storyteller, out var teller))
        {
            return Unassessed("disease_storyteller_unknown:" + facts.Storyteller);
        }

        if (teller.Unmodelled.Count > 0)
        {
            return Unassessed("disease_comp_unmodelled:" + string.Join(",", teller.Unmodelled));
        }

        if (!table.Biomes.TryGetValue(facts.Biome, out var biome))
        {
            return Unassessed("disease_biome_unknown:" + facts.Biome);
        }

        if (!double.IsFinite(facts.IntervalFactor) || !double.IsFinite(facts.DaysSinceSettle) || !(biome.MtbDays > 0))
        {
            return Unassessed("disease_rate_invalid");
        }

        // StorytellerComp_Disease yields nothing with random diseases off or at the difficulty's cutoff; Storyteller.MakeIncidentsForInterval
        // runs a comp only once the days since settling pass its minDaysPassed.
        var mtb = biome.MtbDays * facts.IntervalFactor;
        var comps = !facts.RandomDiseases || facts.IntervalFactor >= DiseaseIntervalFactorCutoff || !(mtb > 0)
            ? []
            : teller.MinDaysPassed.Select(min => (min - facts.DaysSinceSettle, 1 / mtb)).ToArray();

        var rows = new Dictionary<string, DiseaseIncidentRead>(StringComparer.Ordinal);
        foreach (var read in facts.Incidents)
        {
            if (!table.Incidents.ContainsKey(read.Incident))
            {
                return Unassessed("disease_incident_unknown:" + read.Incident);
            }

            rows[read.Incident] = read;
        }

        var usable = new List<(DiseaseIncidentRules Rules, double Weight, int Potential)>();
        var settled = Math.Floor(facts.DaysSinceSettle);
        foreach (var rules in table.Incidents.Values.OrderBy(r => r.Incident, StringComparer.Ordinal))
        {
            if (!rows.TryGetValue(rules.Incident, out var read))
            {
                return Unassessed("disease_victims_unobserved:" + rules.Incident);
            }

            // IncidentWorker.CanFireNow and CanFireNowSub: the home map, earliestDay, the biome lists, minPopulation, the game-state
            // gates the native read reports, and a potential victim; TryRandomElementByWeight never picks a weight of 0.
            var weight = biome.Commonality.GetValueOrDefault(rules.Incident);
            if (!rules.OnHomeMap || settled < rules.EarliestDay || rules.AllowedBiomes.Count > 0 && !rules.AllowedBiomes.Contains(facts.Biome)
                || rules.DisallowedBiomes.Contains(facts.Biome) || rules.MinPopulation > 0 && facts.FreeColonists < rules.MinPopulation
                || read.Blocked.Count > 0 || read.Potential <= 0 || !(weight > 0))
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

            usable.Add((rules, weight, read.Potential));
        }

        var total = usable.Sum(u => u.Weight);
        return new IllnessSchedule
        {
            Assessed = true,
            Comps = comps,
            Incidents = [.. usable.Select(u => new IllnessIncident(u.Rules.Incident, u.Weight / total,
                VictimChance(facts.Candidates, u.Potential, u.Rules.FractionMin, u.Rules.FractionMax, u.Rules.MaxVictims), u.Rules.Course))],
        };
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
