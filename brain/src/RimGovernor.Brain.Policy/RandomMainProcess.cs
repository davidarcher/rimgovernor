using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>The conditional finite native interval process. One shared target clock survives every non-ThreatBig outcome.</summary>
public sealed class RandomMainProcess
{
    public static IReadOnlyList<string> Conditions { get; } =
    [
        "incident_process_conditional_no_other_maps_caravans_or_quests",
        "incident_process_conditional_observed_eligibility_and_successful_spatial_execution",
        "incident_process_trade_may_be_overestimated",
    ];
    private const int CheckTicks = 1000;
    private readonly TradeVisitFacts _facts;
    private readonly IncidentClockRead _clock;
    private readonly IReadOnlyList<(double X, double Y)> _populationChance;
    private readonly Dictionary<(double Origin, EquatableArray<(int Comp, string Incident, double Chance)> Qualify), double[]> _survivors = [];
    private readonly Dictionary<(int Comp, string Incident), double[]> _means = [];
    private readonly double[] _dates;
    private readonly IReadOnlyDictionary<int, IncidentClockReset[]> _resetsByTick;
    private bool _meansRead;

    public RandomMainProcess(TradeVisitFacts facts, IReadOnlyList<(double X, double Y)> populationChance)
    {
        _facts = facts;
        _clock = facts.Clock ?? throw new RequiredFactException("incident_clock_unobserved");
        _populationChance = populationChance;
        _resetsByTick = _clock.Resets.GroupBy(r => checked(_clock.Tick + (int)Math.Round(r.Day * GameTime.TicksPerDay)))
            .ToDictionary(g => g.Key, g => g.ToArray());
        if (_clock.Unmodelled.Count > 0) throw new RequiredFactException("incident_clock_unmodelled:" + string.Join(",", _clock.Unmodelled));
        var ticks = new SortedSet<int>(_clock.Resets.Select(r => checked(_clock.Tick + (int)Math.Round(r.Day * GameTime.TicksPerDay))));
        for (var tick = (_clock.Tick / CheckTicks + 1) * CheckTicks; tick < _clock.Tick + _clock.ThroughDays * GameTime.TicksPerDay; tick += CheckTicks)
            ticks.Add(tick);
        _dates = [.. ticks.Where(t => t >= _clock.Tick).Select(t => (t - _clock.Tick) / (double)GameTime.TicksPerDay)];
    }

    public IReadOnlyList<double> Dates => _dates;

    public bool Contains(int comp) => _clock.RandomMain.Any(c => c.CompIndex == comp) || _clock.Resets.Any(c => c.CompIndex == comp);

    public IEnumerable<(double Day, double Chance)> Emissions(int comp, string incident, double through)
    {
        Require(through);
        if (!_meansRead) { Evaluate(0, new Dictionary<(int Comp, string Incident), double>(), true); _meansRead = true; }
        if (!_means.TryGetValue((comp, incident), out var values)) yield break;
        for (var index = 0; index < _dates.Length && _dates[index] < through; index++)
            if (values[index] > 0) yield return (_dates[index], values[index]);
    }

    public double ExpectedAt(int comp, string incident, double day)
    {
        Require(day);
        if (!_meansRead) { Evaluate(0, new Dictionary<(int Comp, string Incident), double>(), true); _meansRead = true; }
        var at = Array.BinarySearch(_dates, day);
        return at >= 0 && _means.TryGetValue((comp, incident), out var values) ? values[at] : 0;
    }

    public double NotArrived(double origin, double through, IReadOnlyDictionary<(int Comp, string Incident), double> qualifying)
    {
        Require(origin + through);
        var key = (origin, EquatableArray.From(qualifying.OrderBy(p => p.Key.Comp).ThenBy(p => p.Key.Incident, StringComparer.Ordinal)
            .Select(p => (p.Key.Comp, p.Key.Incident, p.Value))));
        if (!_survivors.TryGetValue(key, out var values)) _survivors[key] = values = Evaluate(origin, qualifying, false);
        var index = Array.BinarySearch(_dates, origin + through);
        if (index < 0) index = ~index - 1;
        return index < 0 ? 1 : values[index];
    }

    private void Require(double through)
    {
        if (!double.IsFinite(through) || through < 0 || through > _clock.ThroughDays)
            throw new RequiredFactException("incident_process_window_unobserved");
    }

    private double[] Evaluate(double origin, IReadOnlyDictionary<(int Comp, string Incident), double> qualifying, bool means)
    {
        // Cohorts retain the last-any tick. Only cohorts old enough for this comp participate in its draw.
        // The common native spacing has one active cohort and a release queue. Resets of every cohort
        // share one lazy multiplier, so a local drill/disease event does not visit the cooldown matrix.
        var cap = ThreatAgeCap();
        var spacing = _clock.RandomMain.Select(c => c.MinSpacingTicks).DefaultIfEmpty(0).Max();
        if (_clock.RenewalIncident.Length > 0 || _clock.RandomMain.Any(c => c.MinSpacingTicks != spacing))
            return EvaluateDistinctSpacing(origin, qualifying, means);
        var initial = Math.Max(_clock.Tick - cap, _clock.LastThreatBigTick);
        var total = new Dictionary<int, double> { [initial] = 1 };
        var active = new Dictionary<int, double>();
        var queue = new SortedDictionary<int, Dictionary<int, double>>();
        var scale = 1.0;
        if (_clock.Tick - _clock.LastIncidentTick >= spacing) active[initial] = 1;
        else queue[_clock.LastIncidentTick + spacing] = new() { [initial] = 1 };
        var survivor = new double[_dates.Length];
        var boundaries = _facts.Categories.SelectMany(c => c.Outcomes).SelectMany(o => o.Incidents).SelectMany(i => new[] { i.EarliestDay, i.ExecuteAfterDay }).Distinct().Order().ToArray();
        var draws = new Dictionary<(int Comp, bool Forced, int Era), IReadOnlyDictionary<string, double>>();
        for (var at = 0; at < _dates.Length; at++)
        {
            var day = _dates[at];
            var tick = checked(_clock.Tick + (int)Math.Round(day * GameTime.TicksPerDay));
            total = Collapse(total, tick - cap);
            active = Collapse(active, tick - cap);
            while (queue.Count > 0 && queue.First().Key <= tick)
            {
                var ready = queue.First();
                foreach (var (last, probability) in ready.Value)
                    Add(active, Math.Max(tick - cap, last), probability * scale);
                queue.Remove(ready.Key);
            }
            var settled = _facts.DaysSinceSettle + day;
            var era = Array.FindLastIndex(boundaries, boundary => boundary <= settled);
            var checks = tick % CheckTicks == 0 ? _clock.RandomMain.Where(c => settled > c.MinDaysPassed).ToArray() : [];
            var resets = _resetsByTick.GetValueOrDefault(tick) ?? [];
            foreach (var compIndex in checks.Select(c => c.CompIndex).Concat(resets.Select(r => r.CompIndex)).Distinct().Order())
            {
                foreach (var reset in resets.Where(r => r.CompIndex == compIndex))
                {
                    var outcomes = reset.Outcomes;
                    var chance = reset.Chance * outcomes.Sum(o => o.Chance);
                    var sent = new Dictionary<int, double>();
                    foreach (var outcome in outcomes)
                    {
                        var probability = reset.Chance * outcome.Chance;
                        Emit(compIndex, outcome.Incident, total.Values.Sum() * probability, at);
                        var keep = 1 - (day >= origin ? qualifying.GetValueOrDefault((compIndex, outcome.Incident)) : 0);
                        foreach (var (last, mass) in total)
                            Add(sent, outcome.ResetsThreatBig ? tick : last, mass * probability * keep);
                    }
                    total = total.ToDictionary(p => p.Key, p => p.Value * (1 - chance));
                    foreach (var (last, probability) in sent) Add(total, last, probability);
                    active = active.ToDictionary(p => p.Key, p => p.Value * (1 - chance));
                    if (chance == 1) { queue.Clear(); scale = 1; }
                    else scale *= 1 - chance;
                    Enqueue(sent);
                }
                foreach (var comp in checks.Where(c => c.CompIndex == compIndex))
                {
                    var next = new Dictionary<int, double>();
                    var sentToCooldown = new Dictionary<int, double>();
                    var categories = _facts.Categories.Where(c => c.CompIndex == comp.CompIndex).ToArray();
                    var bigIncidents = categories.Where(c => c.ThreatBig).SelectMany(c => c.Outcomes).SelectMany(o => o.Incidents)
                        .Select(i => i.Incident).ToHashSet(StringComparer.Ordinal);
                    foreach (var (last, probability) in active)
                    {
                        var forced = (long)tick - last > comp.ForceThreatAfterTicks;
                        var key = (comp.CompIndex, forced, era);
                        if (!draws.TryGetValue(key, out var incidents))
                            draws[key] = incidents = Draw(categories, settled, forced);
                        var eventMass = probability * comp.ChancePerCheck;
                        Add(next, last, probability * (1 - comp.ChancePerCheck));
                        Add(total, last, -eventMass);
                        var selected = 0.0;
                        foreach (var (incident, chance) in incidents)
                        {
                            selected += chance;
                            var sent = eventMass * chance;
                            Emit(comp.CompIndex, incident, sent, at);
                            var killed = day >= origin ? qualifying.GetValueOrDefault((comp.CompIndex, incident)) : 0;
                            var clock = bigIncidents.Contains(incident) ? tick : last;
                            Add(sentToCooldown, clock, sent * (1 - killed));
                            Add(total, clock, sent * (1 - killed));
                        }
                        var failed = eventMass * Math.Max(0, 1 - selected);
                        Add(next, last, failed);
                        Add(total, last, failed);
                    }
                    active = next;
                    Enqueue(sentToCooldown);
                }
            }
            survivor[at] = Math.Clamp(total.Values.Sum(), 0, 1);

            void Enqueue(Dictionary<int, double> sent)
            {
                if (spacing == 0)
                {
                    foreach (var (last, probability) in sent) Add(active, last, probability);
                    return;
                }
                // Renormalize only when a long run of resets exhausts the shared multiplier.
                if (scale < 1e-100)
                {
                    foreach (var cohort in queue.Values)
                        foreach (var last in cohort.Keys.ToArray()) cohort[last] *= scale;
                    scale = 1;
                }
                if (!queue.TryGetValue(tick + spacing, out var cohortAt)) queue[tick + spacing] = cohortAt = [];
                foreach (var (last, probability) in sent) Add(cohortAt, last, probability / scale);
            }
        }
        return survivor;

        void Emit(int comp, string incident, double probability, int at)
        {
            if (!means || incident.Length == 0) return;
            if (!_means.TryGetValue((comp, incident), out var emissions))
                _means[(comp, incident)] = emissions = new double[_dates.Length];
            emissions[at] += probability;
        }
    }

    private static void Add(Dictionary<int, double> values, int key, double probability)
    {
        if (probability != 0) values[key] = values.GetValueOrDefault(key) + probability;
    }

    private static Dictionary<int, double> Collapse(Dictionary<int, double> values, int oldest) =>
        values.Where(p => p.Value > 0).GroupBy(p => Math.Max(oldest, p.Key)).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));

    // A custom storyteller can have several RandomMain comps with different spacing thresholds.
    // Keep the same joint process there; the common native threshold above admits the smaller cohort representation.
    private double[] EvaluateDistinctSpacing(double origin, IReadOnlyDictionary<(int Comp, string Incident), double> qualifying, bool means)
    {
        var bigCap = ThreatAgeCap();
        var anyCap = _clock.RandomMain.Select(c => c.MinSpacingTicks).DefaultIfEmpty(0).Max();
        var mass = new Dictionary<(int Big, int Any, int Renewal), double> { [(_clock.LastThreatBigTick, _clock.LastIncidentTick, _clock.LastRenewalTick)] = 1 };
        var survivor = new double[_dates.Length];
        var boundaries = _facts.Categories.SelectMany(c => c.Outcomes).SelectMany(o => o.Incidents).SelectMany(i => new[] { i.EarliestDay, i.ExecuteAfterDay }).Distinct().Order().ToArray();
        var draws = new Dictionary<(int Comp, bool Forced, int Era), IReadOnlyDictionary<string, double>>();
        for (var at = 0; at < _dates.Length; at++)
        {
            var day = _dates[at];
            var tick = checked(_clock.Tick + (int)Math.Round(day * GameTime.TicksPerDay));
            mass = mass.GroupBy(p => (Big: Math.Max(tick - bigCap, p.Key.Big), Any: Math.Max(tick - anyCap, p.Key.Any), Renewal: p.Key.Renewal))
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
            var settled = _facts.DaysSinceSettle + day;
            var era = Array.FindLastIndex(boundaries, boundary => boundary <= settled);
            var checks = tick % CheckTicks == 0 ? _clock.RandomMain.Where(c => settled > c.MinDaysPassed).ToArray() : [];
            var resets = _resetsByTick.GetValueOrDefault(tick) ?? [];
            foreach (var compIndex in checks.Select(c => c.CompIndex).Concat(resets.Select(r => r.CompIndex)).Distinct().Order())
            {
                foreach (var reset in resets.Where(r => r.CompIndex == compIndex))
                {
                    var outcomes = reset.Outcomes;
                    Apply(compIndex, reset, outcomes);
                }
                foreach (var comp in checks.Where(c => c.CompIndex == compIndex))
                {
                    var next = new Dictionary<(int Big, int Any, int Renewal), double>();
                    foreach (var (state, probability) in mass)
                    {
                        if (tick - state.Any < comp.MinSpacingTicks) { AddState(next, state, probability); continue; }
                        var forced = (long)tick - state.Big > comp.ForceThreatAfterTicks;
                        var key = (comp.CompIndex, forced, era);
                        var categories = _facts.Categories.Where(c => c.CompIndex == comp.CompIndex).ToArray();
                        if (!draws.TryGetValue(key, out var incidents)) draws[key] = incidents = Draw(categories, settled, forced);
                        var big = categories.Where(c => c.ThreatBig).SelectMany(c => c.Outcomes).SelectMany(o => o.Incidents).Select(i => i.Incident).ToHashSet(StringComparer.Ordinal);
                        AddState(next, state, probability * (1 - comp.ChancePerCheck * incidents.Values.Sum()));
                        foreach (var (incident, chance) in incidents)
                        {
                            var sent = probability * comp.ChancePerCheck * chance;
                            Emit(compIndex, incident, sent);
                            var keep = 1 - (day >= origin ? qualifying.GetValueOrDefault((compIndex, incident)) : 0);
                            AddState(next, (big.Contains(incident) ? tick : state.Big, tick, incident == _clock.RenewalIncident ? tick : state.Renewal), sent * keep);
                        }
                    }
                    mass = next;
                }
            }
            survivor[at] = Math.Clamp(mass.Values.Sum(), 0, 1);

            void Apply(int compIndex, IncidentClockReset reset, IReadOnlyList<IncidentResetOutcome> outcomes)
            {
                var next = new Dictionary<(int Big, int Any, int Renewal), double>();
                foreach (var (state, probability) in mass)
                {
                    var age = state.Renewal < 0 ? -1 : tick - state.Renewal;
                    var roll = reset.Renewal ? age >= 0 && age < reset.RenewalMinTicks ? 0 : age > reset.RenewalMaxTicks ? 1 : reset.Chance : reset.Chance;
                    AddState(next, state, probability * (1 - roll * outcomes.Sum(o => o.Chance)));
                    foreach (var outcome in outcomes)
                    {
                        var sent = probability * roll * outcome.Chance;
                        Emit(compIndex, outcome.Incident, sent);
                        var keep = 1 - (day >= origin ? qualifying.GetValueOrDefault((compIndex, outcome.Incident)) : 0);
                        AddState(next, (outcome.ResetsThreatBig ? tick : state.Big, tick, outcome.Incident == _clock.RenewalIncident ? tick : state.Renewal), sent * keep);
                    }
                }
                mass = next;
            }
            void Emit(int comp, string incident, double probability)
            {
                if (!means || incident.Length == 0) return;
                if (!_means.TryGetValue((comp, incident), out var emissions)) _means[(comp, incident)] = emissions = new double[_dates.Length];
                emissions[at] += probability;
            }
        }
        return survivor;

        static void AddState(Dictionary<(int Big, int Any, int Renewal), double> values, (int Big, int Any, int Renewal) key, double probability)
        {
            if (probability > 0) values[key] = values.GetValueOrDefault(key) + probability;
        }
    }

    private IReadOnlyDictionary<string, double> Draw(IReadOnlyList<IncidentCategoryRead> reads, double settled, bool forced)
    {
        var categories = new List<IncidentCategoryDraw.Category>();
        foreach (var read in reads)
        {
            if (read.Unmodelled.Count > 0) throw new RequiredFactException("incident_category_unmodelled:" + string.Join(",", read.Unmodelled));
            var incidents = new Dictionary<string, double>(StringComparer.Ordinal);
            var selected = 0.0;
            foreach (var outcome in read.Outcomes)
                foreach (var (incident, chance) in TradeArrivalRules.SelectRandomIncident(
                    [.. outcome.Incidents.Where(i => i.EarliestDay <= settled).Select(i => (i.Incident, i.ChangesPopulation, i.Weight))], _facts.PopulationIntent, _populationChance))
                {
                    selected += outcome.Probability * chance;
                    var chosen = outcome.Incidents.Single(i => i.Incident == incident);
                    var execution = settled >= chosen.ExecuteAfterDay ? chosen.ExecutionChance : 0;
                    incidents[incident] = incidents.GetValueOrDefault(incident) + outcome.Probability * chance * execution;
                }
            categories.Add(new(read.Category, Math.Max(0, read.Weight), incidents) { Success = selected });
        }
        if (!forced) return IncidentCategoryDraw.Draw(categories);
        var bigName = reads.Single(c => c.ThreatBig).Category;
        var big = categories.Single(c => c.Name == bigName);
        var result = new Dictionary<string, double>(big.Incidents, StringComparer.Ordinal);
        foreach (var (incident, chance) in IncidentCategoryDraw.Draw(categories.Where(c => c != big).ToArray()))
            result[incident] = result.GetValueOrDefault(incident) + (1 - big.Success) * chance;
        return result;
    }

    private int ThreatAgeCap()
    {
        var earliestForce = _clock.RandomMain.Select(c => c.ForceThreatAfterTicks).DefaultIfEmpty(int.MaxValue).Min();
        if (_clock.Tick + _clock.ThroughDays * GameTime.TicksPerDay - Math.Min(_clock.Tick, _clock.LastThreatBigTick) <= earliestForce)
            return 0; // No Big history can cross a forcing boundary inside this observed window.
        var maximum = _clock.RandomMain.Select(c => c.ForceThreatAfterTicks).DefaultIfEmpty(0).Max();
        return maximum == int.MaxValue ? maximum : maximum + 1;
    }
}
