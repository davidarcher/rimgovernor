using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>Go <c>ErrSupplyPlanFacts</c>: the supply plan inputs are unavailable or invalid.</summary>
public sealed class SupplyPlanFactsException() : DomainException("supply plan inputs unavailable or invalid");

/// <summary>
/// One good the colony needs delivered. A flow demand (PerDay) is a rate to sustain, such as nutrition; a stock
/// demand (Units) is a deficit to cover once, such as a resource floor. At most one is positive; a demand with
/// neither is satisfied and matches nothing.
/// </summary>
public sealed record SupplyDemand
{
    public ResourceKey Good { get; init; } = new();

    public double PerDay { get; init; }

    public long Units { get; init; }

    /// <summary>1..100; the higher serves first and wins urgent-work contests.</summary>
    public int Priority { get; init; }

    /// <summary>
    /// The longest lead a candidate may have and still serve the demand; 0 is a floor wanted now. It is also
    /// the window an upfront cost is amortised over (at least one day).
    /// </summary>
    public double HorizonDays { get; init; }

    /// <summary>Opens the demand's candidates breadth first, one per kind before a second of any, and credits a candidate only once it delivers.</summary>
    public bool Emergency { get; init; }

    /// <summary>Reported as the target_cover term (nutrition's target coverage).</summary>
    public double Cover { get; init; }

    internal bool Active => PerDay > 0 || Units > 0;

    internal bool Flow => PerDay > 0;

    internal double Window => Math.Max(1, HorizonDays);
}

/// <summary>The food forecast's demand model.</summary>
public sealed record NutritionDemandInput
{
    public FoodForecast Forecast { get; init; } = new();

    public double ReserveDays { get; init; }

    public double MinDays { get; init; }

    public double TargetDays { get; init; }

    /// <summary>The stage's starvation line (FootholdFoodDays); zero disables the emergency.</summary>
    public double EmergencyDays { get; init; }
}

/// <summary>
/// The input of PlanSupply. Labor is the shared budget in work ticks per day (workers * 20000). UrgentPriority
/// is competing urgent work: it holds a candidate whose best matched demand ranks below it.
/// </summary>
public sealed record SupplyPlanRequest
{
    public Fact<IReadOnlyList<SupplyDemand>> Demands { get; init; }

    public Fact<IReadOnlyList<SupplyCandidate>> Candidates { get; init; }

    public Fact<double> Labor { get; init; }

    public int UrgentPriority { get; init; }

    /// <summary>
    /// Each ingredient resource's units a produce candidate may spend: stock above what the plan protects. A
    /// candidate whose UpfrontCost.Resources names a resource absent here is unknown; the draws of the
    /// candidates opened are subtracted in rank order, so a bill cannot spend below the protected line however
    /// many bills compete.
    /// </summary>
    public IReadOnlyList<ResourceQuantity> Usable { get; init; } = [];
}

public enum SupplyDecision
{
    Open,
    Hold,
    Close,
}

/// <summary>The contribution admitted toward one demand: risk-adjusted units per day for a flow, units for a stock demand.</summary>
public sealed record SupplyCredit(ResourceKey Good, double Amount);

/// <summary>
/// The decision for one candidate. Score, Wanted, Trips and Value price the candidate against the full demands,
/// independent of the other candidates: covered value per labor-equivalent cost.
/// </summary>
public sealed class SupplyEntry
{
    private readonly List<CandidateTerm> _terms = [];
    private List<SupplyCredit> _credit = [];

    public required SupplyCandidate Candidate { get; init; }

    /// <summary>Hold preserves current state: keep a delivering candidate or defer a closed one.</summary>
    public SupplyDecision Decision { get; internal set; } = SupplyDecision.Hold;

    public string Reason { get; internal set; } = "";

    public IReadOnlyList<CandidateTerm> Terms => _terms;

    public IReadOnlyList<SupplyCredit> Credit => _credit;

    public double Score { get; internal set; }

    public long Wanted { get; internal set; }

    public long Trips { get; internal set; }

    public double Value { get; internal set; }

    internal void AddTerm(string name, double value) => _terms.Add(new CandidateTerm(name, value));

    internal void AddTerms(IEnumerable<CandidateTerm> terms) => _terms.AddRange(terms);

    internal void AddCredit(SupplyCredit credit) => _credit.Add(credit);

    internal void ClearCredit() => _credit = [];

    internal void SetCreditAmount(int index, double amount) => _credit[index] = _credit[index] with { Amount = amount };
}

/// <summary>What the plan admits against one demand.</summary>
public sealed record SupplyDemandResult(SupplyDemand Demand, double Delivered, double Gap);

/// <summary>The portfolio in rank order and, apart, the candidates with unknown facts.</summary>
public sealed class SupplyPlan
{
    public IReadOnlyList<SupplyDemandResult> Demands { get; init; } = [];

    public IReadOnlyList<SupplyEntry> Portfolio { get; init; } = [];

    public IReadOnlyList<SupplyEntry> Unknown { get; init; } = [];

    /// <summary>What the plan admits toward a good, summed over its demands.</summary>
    public double Delivered(ResourceKey good)
    {
        var total = 0.0;
        foreach (var d in Demands)
        {
            if (d.Demand.Good == good)
            {
                total += d.Delivered;
            }
        }
        return total;
    }

    /// <summary>What a good's demands still lack after admission.</summary>
    public double Gap(ResourceKey good)
    {
        var total = 0.0;
        foreach (var d in Demands)
        {
            if (d.Demand.Good == good)
            {
                total += d.Gap;
            }
        }
        return total;
    }

    public string Explain()
    {
        static string F4(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
        var b = new StringBuilder();
        foreach (var d in Demands)
        {
            b.Append(d.Demand.Good.Def.Value).Append(" delivered=").Append(F4(d.Delivered)).Append(" gap=").Append(F4(d.Gap)).Append('\n');
        }
        foreach (var rows in new[] { Portfolio, Unknown })
        {
            foreach (var e in rows)
            {
                b.Append(' ').Append(Wire.Name(e.Candidate.Kind)).Append('/').Append(e.Candidate.ID).Append(' ').Append(e.Decision).Append(": ").Append(e.Reason);
                foreach (var c in e.Credit)
                {
                    b.Append(" credit[").Append(c.Good.Def.Value).Append("]=").Append(F4(c.Amount));
                }
                foreach (var t in e.Terms)
                {
                    b.Append(' ').Append(t.Name).Append('=').Append(F4(t.Value));
                }
                b.Append('\n');
            }
        }
        return b.ToString().TrimEnd('\n');
    }
}

/// <summary>What a candidate covers of the demands.</summary>
internal sealed class SupplyShare(int demands)
{
    /// <summary>Per demand, risk-adjusted.</summary>
    public double[] Credit { get; } = new double[demands];

    /// <summary>Per demand: a yield matches an active demand.</summary>
    public bool[] Matched { get; } = new bool[demands];

    public long Wanted { get; set; }

    public long Trips { get; set; }

    public double Value { get; set; }

    public int Priority { get; set; }

    /// <summary>A stock demand with units left matched a yield holding some.</summary>
    public bool UnitMatched { get; set; }
}

/// <summary>A validated candidate with the numbers the ranker reads.</summary>
internal sealed class SupplyCand
{
    public required SupplyCandidate C { get; init; }

    /// <summary>Canonical order.</summary>
    public required List<CandidateYield> Yields { get; init; }

    public bool Open { get; init; }

    public double Lead { get; init; }

    public double Work { get; set; }

    public double Upfront { get; init; }

    public double Risk { get; init; }

    public double Dist { get; init; }

    public Fact<double> DistSq { get; init; }

    /// <summary>Priced against the full demands.</summary>
    public SupplyShare Full { get; set; } = new(0);

    /// <summary>Per demand: a flow, or lead within the stock demand's horizon.</summary>
    public bool[] Ok { get; set; } = [];

    /// <summary>Matches a flow demand.</summary>
    public bool Flow { get; set; }

    public int Priority { get; set; }

    public double LaborNew { get; set; }

    /// <summary>Fraction of the stock cap the ingredient draws leave affordable.</summary>
    public double Scale { get; set; } = 1;

    /// <summary>Labor-equivalent per covered value; +Inf covers nothing.</summary>
    public double Cost { get; set; }

    public double[] Credited { get; set; } = [];

    /// <summary>Some yield is a stock cap: the source runs out.</summary>
    public bool Finite { get; init; }

    public required SupplyEntry Entry { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Ranks nutrition above every resource deficit.</summary>
    public const int NutritionPriority = 100;

    /// <summary>The demand and yield key of nutrition.</summary>
    public static ResourceKey NutritionKey { get; } = new() { Def = CandidateNutrition };

    /// <summary>
    /// The nutrition flow demand. Usable runway is max(0, forecast runway - reserve). Below MinDays the horizon
    /// is that usable runway; otherwise it is at least TargetDays, so slower capacity can be established. Target
    /// coverage is 1 + max(0, TargetDays-usable)/TargetDays: replenish the missing buffer over one target window
    /// while also feeding consumers.
    /// </summary>
    public static SupplyDemand NutritionDemand(NutritionDemandInput input)
    {
        if (!input.Forecast.RunwayDays.TryGetValue(out var runway)
            || !FoodNumber(runway)
            || !FoodNumber(input.ReserveDays)
            || !FoodNumber(input.MinDays)
            || !FoodNumber(input.TargetDays)
            || input.TargetDays <= input.MinDays
            || input.Forecast.Consumers.Count == 0)
        {
            throw new SupplyPlanFactsException();
        }
        var consumers = new HashSet<PawnId>();
        var perDay = 0.0;
        foreach (var c in input.Forecast.Consumers)
        {
            if (!FoodId(c.Id.Value)
                || consumers.Contains(c.Id)
                || !FoodNumber(c.NutritionPerDay)
                || !FoodNumber(c.RunwayDays)
                || !FoodNumber(c.UsableNutrition)
                || !FoodNumber(c.AllocatedNutrition))
            {
                throw new SupplyPlanFactsException();
            }
            consumers.Add(c.Id);
            perDay += c.NutritionPerDay;
        }
        var f = input.Forecast;
        if (!FoodNumber(perDay) || perDay == 0 || !FoodNumber(f.UsableNutrition) || !FoodNumber(f.AtRiskNutrition) || !FoodNumber(f.InventoryNutrition))
        {
            throw new SupplyPlanFactsException();
        }
        var usable = Math.Max(0, runway - input.ReserveDays);
        var cover = 1 + (RunwayShortfall(usable, input.TargetDays) / input.TargetDays);
        var target = perDay * cover;
        if (!FoodNumber(target))
        {
            throw new SupplyPlanFactsException();
        }
        var horizon = usable;
        if (usable >= input.MinDays)
        {
            horizon = Math.Max(horizon, input.TargetDays);
        }
        return new SupplyDemand
        {
            Good = NutritionKey,
            PerDay = target,
            Priority = NutritionPriority,
            HorizonDays = horizon,
            Emergency = input.EmergencyDays > 0 && runway < input.EmergencyDays,
            Cover = cover,
        };
    }

    /// <summary>A resource deficit as a stock demand wanted now.</summary>
    public static SupplyDemand SupplyDemandOfResource(ResourceDemand d) => new()
    {
        Good = d.Key,
        Units = d.Count,
        Priority = d.Priority,
    };

    private static bool ValidCandidateKind(CandidateKind k) =>
        k is CandidateKind.Forage
            or CandidateKind.Hunt
            or CandidateKind.Slaughter
            or CandidateKind.Crop
            or CandidateKind.AnimalProduct
            or CandidateKind.Fishing
            or CandidateKind.Trade
            or CandidateKind.Corpse
            or CandidateKind.Reserve
            or CandidateKind.Cook
            or CandidateKind.Loot
            or CandidateKind.Salvage
            or CandidateKind.Mining
            or CandidateKind.Produce
            or CandidateKind.DeepDrill
            or CandidateKind.Chop
            or CandidateKind.Harvest
            or CandidateKind.Tame
            or CandidateKind.AnimalBuy;

    private static bool ValidCandidateRisk(CandidateRiskKind k) =>
        k is CandidateRiskKind.Blight or CandidateRiskKind.Fallout or CandidateRiskKind.Frost or CandidateRiskKind.Revenge;

    private static string GoodName(ResourceKey k) => k.Def.Value.ToLowerInvariant();

    private static bool DemandMatches(SupplyDemand d, CandidateYield y) =>
        d.Good.Def == y.Good.Def && (d.Good.Stuff.IsEmpty || d.Good.Stuff == y.Good.Stuff);

    private static int SupplyCompare(SupplyCandidate a, SupplyCandidate b)
    {
        if (a.Kind != b.Kind)
        {
            return a.Kind < b.Kind ? -1 : 1;
        }
        return string.CompareOrdinal(a.ID, b.ID);
    }

    private static int AcquisitionKeyCompare(ResourceKey a, ResourceKey b) =>
        AcquisitionKeyLess(a, b) ? -1 : AcquisitionKeyLess(b, a) ? 1 : 0;

    /// <summary>
    /// The one ranker over a demand vector and unified candidates. It budgets projected rates and unit
    /// deficits, never stored goods or completed work.
    ///
    /// A candidate serves a demand when a yield matches it and, for a stock demand, the candidate's lead is
    /// within the demand's horizon; a flow demand takes any lead. Its contribution to a flow is its risk-adjusted
    /// rate, <c>rate * max(0, 1 - sum(risk))</c>, bounded by its stock cap spread over the demand's window; to a
    /// stock demand a one-shot source contributes min(stock cap, storage headroom when hauled, remaining
    /// deficit). An upfront cost is charged as labor over the demand's window. Labor is charged once however
    /// many demands a candidate serves, and the budget is shared by all demands.
    ///
    /// Order: flow demands (nutrition) before stock demands. Within a flow demand lead is the primary key (a
    /// channel arriving before the runway expires beats a cheaper later one), then fishing distance, then labor
    /// per unit delivered. Within stock demands the key is the score
    /// Value / (1 + labor + distance*(1+2*trips) + trips), then lead, kind, id. An emergency demand re-ranks its
    /// candidates breadth first by kind.
    ///
    /// Delivering candidates keep their observed contribution even over the labor budget (the excess is a Hold
    /// with a labor_excess term), and a strict surplus closes the least efficient first. Closed candidates over
    /// budget add nothing. Unknown labor holds new commitments without changing observed delivery or reporting
    /// numeric labor excess; independently established surplus still closes.
    /// </summary>
    public static SupplyPlan PlanSupply(SupplyPlanRequest r)
    {
        var known = r.Candidates.TryGetValue(out var rows);
        var lk = r.Labor.TryGetValue(out var labor);
        var dk = r.Demands.TryGetValue(out var demandRows);
        if (!known || (lk && !FoodNumber(labor)) || r.UrgentPriority < 0 || r.UrgentPriority > 100)
        {
            throw new SupplyPlanFactsException();
        }
        var demands = new List<SupplyDemand>(dk ? demandRows! : []);
        demands.Sort((a, b) => AcquisitionKeyCompare(a.Good, b.Good));
        for (var i = 0; i < demands.Count; i++)
        {
            var d = demands[i];
            if (!ValidAcquisitionKey(d.Good)
                || !FoodNumber(d.PerDay)
                || !ValidAcquisitionCount(d.Units)
                || (d.PerDay > 0 && d.Units > 0)
                || d.Priority < 1
                || d.Priority > 100
                || !FoodNumber(d.HorizonDays)
                || !FoodNumber(d.Cover)
                || (i > 0 && demands[i - 1].Good == d.Good))
            {
                throw new SupplyPlanFactsException();
            }
        }
        var remaining = new long[demands.Count];
        var delivered = new double[demands.Count];
        var finite = new double[demands.Count];
        for (var i = 0; i < demands.Count; i++)
        {
            remaining[i] = demands[i].Units;
        }
        var usable = new Dictionary<ResourceKey, long>();
        foreach (var q in r.Usable)
        {
            if (usable.ContainsKey(q.Key) || !ValidAcquisitionKey(q.Key) || !ValidAcquisitionCount(q.Count))
            {
                throw new SupplyPlanFactsException();
            }
            usable[q.Key] = q.Count;
        }

        var unknown = new List<SupplyEntry>();
        var cands = new List<SupplyCand>();
        var identities = new HashSet<(CandidateKind, string)>();
        foreach (var row in rows!)
        {
            var (c, missing) = NewSupplyCand(row, identities);
            foreach (var q in c.C.UpfrontCost.Resources)
            {
                if (!usable.ContainsKey(q.Key))
                {
                    missing.Add(GoodName(q.Key) + "_usable");
                }
            }
            if (missing.Count > 0 || !dk)
            {
                c.Entry.Reason = "unknown: " + string.Join(", ", missing);
                if (!dk)
                {
                    c.Entry.Reason = "unknown_demand_or_cost";
                }
                unknown.Add(c.Entry);
                continue;
            }
            Price(c, demands);
            cands.Add(c);
        }
        unknown = [.. unknown.OrderBy(e => e.Candidate, Comparer<SupplyCandidate>.Create(SupplyCompare))];
        cands = [.. cands.OrderBy(c => c, Comparer<SupplyCand>.Create(Before))];
        cands = BreadthFirst(cands, demands);

        var used = 0.0;
        var openOrder = new List<SupplyCand>();
        foreach (var c in cands)
        {
            if (c.Open)
            {
                c.Entry.Reason = "already delivering";
                Admit(c, Share(c, demands, remaining), false, demands, remaining, delivered, finite);
                used += c.Work;
                openOrder.Add(c);
            }
        }
        if (!FoodNumber(used))
        {
            throw new SupplyPlanFactsException();
        }
        openOrder = [.. openOrder.OrderByDescending(c => c.Cost)];
        foreach (var c in openOrder)
        {
            if (Surplus(c, demands, delivered))
            {
                for (var i = 0; i < c.Credited.Length; i++)
                {
                    delivered[i] -= c.Credited[i];
                    if (c.Finite)
                    {
                        finite[i] -= c.Credited[i];
                    }
                }
                c.Entry.Decision = SupplyDecision.Close;
                c.Entry.Reason = "surplus";
                c.Entry.ClearCredit();
                used -= c.Work;
            }
        }
        foreach (var c in openOrder)
        {
            if (c.Entry.Decision != SupplyDecision.Close)
            {
                used -= Trim(c, demands, delivered, finite);
            }
        }
        var portfolio = new List<SupplyEntry>();
        foreach (var c in cands)
        {
            if (c.Open)
            {
                if (c.Entry.Decision != SupplyDecision.Close && lk && used > labor)
                {
                    c.Entry.AddTerm("labor_excess", used - labor);
                }
            }
            else if (!lk)
            {
                c.Entry.Reason = "unknown_capacity";
            }
            else
            {
                used += Consider(c, demands, remaining, delivered, finite, usable, used, labor, r.UrgentPriority);
            }
            portfolio.Add(c.Entry);
        }
        var results = new List<SupplyDemandResult>();
        for (var i = 0; i < demands.Count; i++)
        {
            var d = demands[i];
            var gap = d.PerDay - delivered[i];
            if (!d.Flow)
            {
                gap = remaining[i];
            }
            if (!FoodNumber(delivered[i]) || double.IsInfinity(gap) || double.IsNaN(gap))
            {
                throw new SupplyPlanFactsException();
            }
            results.Add(new SupplyDemandResult(d, delivered[i], gap));
        }
        return new SupplyPlan { Demands = results, Portfolio = portfolio, Unknown = unknown };
    }

    /// <summary>Validates a candidate and reads its facts; the returned list names the unknown ones.</summary>
    private static (SupplyCand Cand, List<string> Missing) NewSupplyCand(SupplyCandidate row, HashSet<(CandidateKind, string)> identities)
    {
        if (!ValidCandidateKind(row.Kind)
            || !FoodId(row.ID)
            || identities.Contains((row.Kind, row.ID))
            || row.Yields.Count == 0
            || !ValidAcquisitionCount(row.UnitsPerTrip)
            || (row.NeedsHaul && row.UnitsPerTrip == 0))
        {
            throw new SupplyPlanFactsException();
        }
        identities.Add((row.Kind, row.ID));
        static (double Value, bool Ok) Num(Fact<double> f, double max)
        {
            var ok = f.TryGetValue(out var v);
            if (ok && (!FoodNumber(v) || v > max))
            {
                throw new SupplyPlanFactsException();
            }
            return (v, ok);
        }
        var (lead, leadKnown) = Num(row.LeadDays, double.MaxValue);
        var (work, workKnown) = Num(row.LaborPerDay, double.MaxValue);
        var (upfront, upKnown) = Num(row.UpfrontCost.LaborTicks, 1e12);
        var (dist, pathKnown) = Num(row.PathDistance, 1e12);
        Num(row.DistanceSquared, double.MaxValue);
        foreach (var q in row.UpfrontCost.Resources)
        {
            if (!ValidAcquisitionKey(q.Key) || !ValidAcquisitionCount(q.Count))
            {
                throw new SupplyPlanFactsException();
            }
        }
        var yields = new List<CandidateYield>(row.Yields);
        yields.Sort((a, b) => AcquisitionKeyCompare(a.Good, b.Good));
        var missing = new List<string>();
        for (var i = 0; i < yields.Count; i++)
        {
            var y = yields[i];
            var (_, rateKnown) = Num(y.PerDay, double.MaxValue);
            var capKnown = y.StockCap.TryGetValue(out var stock);
            var roomKnown = y.Headroom.TryGetValue(out var headroom);
            if (!ValidAcquisitionKey(y.Good)
                || (capKnown && !ValidAcquisitionCount(stock))
                || (roomKnown && !ValidAcquisitionCount(headroom))
                || !FiniteAcquisitionCost(y.UnitValue)
                || (i > 0 && yields[i - 1].Good == y.Good))
            {
                throw new SupplyPlanFactsException();
            }
            if (!rateKnown && !capKnown)
            {
                missing.Add(GoodName(y.Good) + "_per_day");
            }
        }
        if (!workKnown && !upKnown)
        {
            missing.Add("work_per_day");
        }
        if (!leadKnown)
        {
            missing.Add("lead_days");
        }
        var stateKnown = row.State.TryGetValue(out var state);
        if (!stateKnown)
        {
            missing.Add("open");
        }
        // A one-shot source prices its distance; a steady flow is priced by labor.
        if (!pathKnown && (row.NeedsHaul || !workKnown))
        {
            missing.Add("path_distance");
        }
        var risk = 0.0;
        var risks = new HashSet<CandidateRiskKind>();
        foreach (var v in row.Risk)
        {
            if (!ValidCandidateRisk(v.Kind) || risks.Contains(v.Kind) || !FoodNumber(v.Weight) || v.Weight > 1)
            {
                throw new SupplyPlanFactsException();
            }
            risks.Add(v.Kind);
            risk += v.Weight;
        }
        foreach (var v in row.Terms)
        {
            if (!FoodId(v.Name) || double.IsNaN(v.Value) || double.IsInfinity(v.Value))
            {
                throw new SupplyPlanFactsException();
            }
        }
        var finite = false;
        foreach (var y in yields)
        {
            if (y.StockCap.IsKnown)
            {
                finite = true;
            }
        }
        var entry = new SupplyEntry { Candidate = row };
        entry.AddTerms(row.Terms);
        var c = new SupplyCand
        {
            C = row,
            Scale = 1,
            Finite = finite,
            Yields = yields,
            Open = stateKnown && state == CandidateState.Delivering,
            Lead = lead,
            Work = work,
            Upfront = upfront,
            Risk = risk,
            Dist = dist,
            DistSq = row.DistanceSquared,
            Entry = entry,
        };
        return (c, missing);
    }

    /// <summary>A yield's risk-adjusted steady contribution toward a flow demand.</summary>
    private static double Rate(SupplyCand c, CandidateYield y, SupplyDemand d)
    {
        var rateKnown = y.PerDay.TryGetValue(out var rate);
        var capKnown = y.StockCap.TryGetValue(out var stock);
        if (rateKnown && capKnown)
        {
            rate = Math.Min(rate, stock / d.Window);
        }
        else if (capKnown)
        {
            rate = stock / d.Window;
        }
        return rate * Math.Max(0, 1 - c.Risk);
    }

    /// <summary>What a yield can deliver toward a stock demand.</summary>
    private static long Units(SupplyCand c, CandidateYield y, SupplyDemand d)
    {
        if (y.StockCap.TryGetValue(out var stock))
        {
            return (long)Math.Floor((stock * c.Scale) + 1e-9);
        }
        y.PerDay.TryGetValue(out var rate);
        return (long)Math.Floor((rate * Math.Max(0, 1 - c.Risk) * Math.Max(0, d.Window - c.Lead)) + 1e-9);
    }

    /// <summary>
    /// Prices the candidate against demands with remaining units left on each stock demand. Lead is not
    /// applied: Ok says where it is within horizon.
    /// </summary>
    private static SupplyShare Share(SupplyCand c, List<SupplyDemand> demands, long[] remaining)
    {
        var s = new SupplyShare(demands.Count);
        var left = (long[])remaining.Clone();
        foreach (var y in c.Yields)
        {
            var used = 0L;
            for (var i = 0; i < demands.Count; i++)
            {
                var d = demands[i];
                if (!d.Active || !DemandMatches(d, y))
                {
                    continue;
                }
                s.Matched[i] = true;
                if (d.Flow)
                {
                    var amount = Rate(c, y, d);
                    s.Credit[i] += amount;
                    s.Value += amount * d.Priority * (1 + y.UnitValue);
                    if (amount > 0)
                    {
                        s.Priority = Math.Max(s.Priority, d.Priority);
                    }
                    continue;
                }
                var capacity = Units(c, y, d);
                if (capacity > 0 && left[i] > 0)
                {
                    s.UnitMatched = true;
                }
                if (c.C.NeedsHaul)
                {
                    y.Headroom.TryGetValue(out var room);
                    capacity = Math.Min(capacity, room);
                }
                var count = Math.Min(capacity - used, left[i]);
                if (count <= 0)
                {
                    continue;
                }
                left[i] -= count;
                used += count;
                s.Credit[i] += count;
                s.Wanted += count;
                s.Priority = Math.Max(s.Priority, d.Priority);
                s.Value += count * (double)d.Priority * (1 + y.UnitValue);
            }
            if (c.C.NeedsHaul && used > 0)
            {
                s.Trips += 1 + ((used - 1) / c.C.UnitsPerTrip);
            }
        }
        return s;
    }

    /// <summary>Fills the candidate's rank keys and explain terms from the full demands.</summary>
    private static void Price(SupplyCand c, List<SupplyDemand> demands)
    {
        var remaining = new long[demands.Count];
        for (var i = 0; i < demands.Count; i++)
        {
            remaining[i] = demands[i].Units;
        }
        c.Full = Share(c, demands, remaining);
        c.Ok = new bool[demands.Count];
        var window = 1.0;
        var covered = 0.0;
        for (var i = 0; i < demands.Count; i++)
        {
            var d = demands[i];
            // A flow has no deadline: a slow channel costs a little labor now and then only time, so lead
            // orders the candidates (fast first) and the labor budget limits them, instead of a horizon
            // shutting the slow ones out.
            c.Ok[i] = d.Flow || c.Lead <= d.HorizonDays;
            if (!c.Full.Matched[i])
            {
                continue;
            }
            c.Priority = Math.Max(c.Priority, d.Priority);
            window = Math.Max(window, d.Window);
            if (d.Flow)
            {
                c.Flow = true;
                covered += c.Full.Credit[i];
            }
        }
        c.LaborNew = c.Work + (c.Upfront / window);
        var haul = c.Dist * (1 + (2 * (double)c.Full.Trips));
        var denominator = 1 + c.LaborNew + haul + c.Full.Trips;
        var e = c.Entry;
        e.Wanted = c.Full.Wanted;
        e.Trips = c.Full.Trips;
        e.Value = c.Full.Value;
        e.Score = c.Full.Value / denominator;
        c.Cost = double.PositiveInfinity;
        if (c.Flow && covered > 0)
        {
            c.Cost = c.LaborNew / covered;
        }
        else if (!c.Flow && c.Full.Value > 0)
        {
            c.Cost = denominator / c.Full.Value;
        }
        e.AddTerm("risk_discount", Math.Min(1, c.Risk));
        var cover = 0.0;
        for (var i = 0; i < demands.Count; i++)
        {
            var d = demands[i];
            if (!c.Full.Matched[i])
            {
                continue;
            }
            var suffix = "_units";
            if (d.Flow)
            {
                suffix = "_per_day";
                if (cover == 0)
                {
                    cover = d.Cover;
                }
            }
            e.AddTerm(GoodName(d.Good) + suffix, c.Full.Credit[i]);
        }
        e.AddTerm("work_per_day", c.Work);
        e.AddTerm("lead_days", c.Lead);
        if (cover > 0)
        {
            e.AddTerm("target_cover", cover);
        }
    }

    private static int Before(SupplyCand c, SupplyCand o)
    {
        if (c.Flow != o.Flow)
        {
            return c.Flow ? -1 : 1;
        }
        if (c.Flow)
        {
            if (c.Lead != o.Lead)
            {
                return c.Lead < o.Lead ? -1 : 1;
            }
            if (c.C.Kind == CandidateKind.Fishing && o.C.Kind == CandidateKind.Fishing)
            {
                var ak = c.DistSq.TryGetValue(out var da);
                var bk = o.DistSq.TryGetValue(out var db);
                if (ak != bk)
                {
                    return ak ? -1 : 1;
                }
                if (da != db)
                {
                    return da < db ? -1 : 1;
                }
            }
            if (c.Cost != o.Cost)
            {
                return c.Cost < o.Cost ? -1 : 1;
            }
        }
        else
        {
            if (c.Entry.Score != o.Entry.Score)
            {
                return c.Entry.Score > o.Entry.Score ? -1 : 1;
            }
            if (c.Lead != o.Lead)
            {
                return c.Lead < o.Lead ? -1 : 1;
            }
        }
        return SupplyCompare(c.C, o.C);
    }

    /// <summary>
    /// Reorders the candidates serving an emergency demand, within their own slots: the best of every kind, then
    /// the second, and so on.
    /// </summary>
    private static List<SupplyCand> BreadthFirst(List<SupplyCand> cands, List<SupplyDemand> demands)
    {
        var slots = new List<int>();
        for (var i = 0; i < cands.Count; i++)
        {
            for (var k = 0; k < demands.Count; k++)
            {
                if (demands[k].Emergency && demands[k].Flow && cands[i].Full.Matched[k])
                {
                    slots.Add(i);
                    break;
                }
            }
        }
        var rank = new int[slots.Count];
        var kinds = new Dictionary<CandidateKind, int>();
        for (var n = 0; n < slots.Count; n++)
        {
            var kind = cands[slots[n]].C.Kind;
            kinds.TryGetValue(kind, out var seen);
            rank[n] = seen;
            kinds[kind] = seen + 1;
        }
        var order = Enumerable.Range(0, slots.Count).OrderBy(n => rank[n]).ToList();
        var output = new List<SupplyCand>(cands);
        for (var n = 0; n < order.Count; n++)
        {
            output[slots[n]] = cands[slots[order[n]]];
        }
        return output;
    }

    /// <summary>
    /// Credits the share toward the demands. Under an emergency a candidate that is not yet delivering is not
    /// food until delivered, so it covers nothing.
    /// </summary>
    private static void Admit(SupplyCand c, SupplyShare s, bool opening, List<SupplyDemand> demands, long[] remaining, double[] delivered, double[] finite)
    {
        c.Credited = new double[demands.Count];
        for (var i = 0; i < demands.Count; i++)
        {
            var d = demands[i];
            var amount = s.Credit[i];
            if (amount == 0 || (opening && !c.Ok[i]))
            {
                continue;
            }
            if (opening && d.Emergency && d.Flow)
            {
                c.Entry.AddTerm("uncredited_until_delivered", amount);
                continue;
            }
            c.Credited[i] = amount;
            delivered[i] += amount;
            if (c.Finite)
            {
                finite[i] += amount;
            }
            if (!d.Flow)
            {
                remaining[i] -= (long)amount;
            }
            c.Entry.AddCredit(new SupplyCredit(d.Good, amount));
        }
    }

    /// <summary>
    /// Holds when every demand the candidate feeds is a flow that stays strictly above its target without it:
    /// closure needs a surplus greater than the contribution, which prevents churn at the boundary.
    /// </summary>
    private static bool Surplus(SupplyCand c, List<SupplyDemand> demands, double[] delivered)
    {
        var feeds = false;
        for (var i = 0; i < c.Credited.Length; i++)
        {
            var amount = c.Credited[i];
            if (amount == 0)
            {
                continue;
            }
            feeds = true;
            if (!demands[i].Flow || delivered[i] - demands[i].PerDay <= amount)
            {
                return false;
            }
        }
        return feeds;
    }

    /// <summary>Decides a closed candidate and returns the labor it takes.</summary>
    private static double Consider(
        SupplyCand c,
        List<SupplyDemand> demands,
        long[] remaining,
        double[] delivered,
        double[] finite,
        Dictionary<ResourceKey, long> usable,
        double used,
        double labor,
        int urgent
    )
    {
        var e = c.Entry;
        var f = c.Full;
        var total = 0.0;
        var flow = false;
        var anyOk = false;
        for (var i = 0; i < demands.Count; i++)
        {
            total += f.Credit[i];
            flow = flow || (f.Matched[i] && demands[i].Flow);
            anyOk = anyOk || (f.Matched[i] && c.Ok[i]);
        }
        if (total == 0 && flow)
        {
            e.Reason = "no risk-adjusted " + GoodName(FlowGood(c, demands));
            return 0;
        }
        if (total == 0 && f.UnitMatched)
        {
            e.Reason = "no_storage_headroom";
            return 0;
        }
        if (total == 0)
        {
            e.Reason = "no_demand";
            return 0;
        }
        if (!anyOk)
        {
            e.Reason = "lead exceeds runway";
            return 0;
        }
        if (urgent > f.Priority)
        {
            e.Reason = "competing_urgent_work";
            return 0;
        }
        c.Scale = Affordable(c, usable);
        var share = Share(c, demands, remaining);
        if (c.Scale < 1)
        {
            e.AddTerm("ingredient_scale", c.Scale);
            if (share.Credit.Sum() == 0)
            {
                e.Reason = "ingredient_unavailable";
                return 0;
            }
        }
        var needs = false;
        var good = "";
        for (var i = 0; i < demands.Count; i++)
        {
            var d = demands[i];
            if (f.Matched[i] && c.Ok[i] && share.Credit[i] > 0 && (!d.Flow || Covered(c, delivered[i], finite[i]) < d.PerDay))
            {
                if (!needs)
                {
                    good = GoodName(d.Good);
                }
                needs = true;
            }
        }
        if (!needs)
        {
            e.Reason = "target covered";
            return 0;
        }
        if (ScalesToGap(c))
        {
            var frac = 1.0;
            for (var i = 0; i < demands.Count; i++)
            {
                var d = demands[i];
                if (f.Matched[i] && c.Ok[i] && d.Flow && share.Credit[i] > 0)
                {
                    var gap = d.PerDay - Covered(c, delivered[i], finite[i]);
                    if (gap < share.Credit[i])
                    {
                        frac = Math.Min(frac, gap / share.Credit[i]);
                    }
                }
            }
            if (frac < 1)
            {
                for (var i = 0; i < demands.Count; i++)
                {
                    if (demands[i].Flow)
                    {
                        share.Credit[i] *= frac;
                    }
                }
                c.LaborNew -= c.Work * (1 - frac);
                c.Work *= frac;
                e.AddTerm("gap_share", frac);
            }
        }
        var free = Math.Max(0, labor - used);
        if (c.LaborNew > free)
        {
            e.Reason = "labor budget";
            e.AddTerm("labor_excess", c.LaborNew - free);
            return 0;
        }
        e.Decision = SupplyDecision.Open;
        e.Reason = "close " + good + " gap";
        Admit(c, share, true, demands, remaining, delivered, finite);
        foreach (var q in c.C.UpfrontCost.Resources)
        {
            usable.TryGetValue(q.Key, out var have);
            usable[q.Key] = have - Math.Min(have, (long)Math.Ceiling((q.Count * c.Scale) - 1e-9));
        }
        return c.LaborNew;
    }

    /// <summary>A source whose draw is a dial: fishing takes as many fish as the open gap needs, so its credit and labor are capped at that gap.</summary>
    private static bool ScalesToGap(SupplyCand c) => c.C.Kind == CandidateKind.Fishing;

    /// <summary>
    /// Scales an open gap-scaled source down to the flow demand's remaining need, freeing the labor it was
    /// booked at the full draw. It returns the labor freed. A need already met by other steady sources is left
    /// to surplus.
    /// </summary>
    private static double Trim(SupplyCand c, List<SupplyDemand> demands, double[] delivered, double[] finite)
    {
        var freed = 0.0;
        for (var i = 0; i < c.Credited.Length; i++)
        {
            var amount = c.Credited[i];
            var d = demands[i];
            if (amount <= 0 || !d.Flow || !ScalesToGap(c))
            {
                continue;
            }
            var need = d.PerDay - (Covered(c, delivered[i], finite[i]) - amount);
            if (need <= 0 || need >= amount)
            {
                continue;
            }
            var frac = need / amount;
            c.Credited[i] = need;
            delivered[i] -= amount - need;
            for (var j = 0; j < c.Entry.Credit.Count; j++)
            {
                if (c.Entry.Credit[j].Good == d.Good)
                {
                    c.Entry.SetCreditAmount(j, need);
                }
            }
            freed += c.Work * (1 - frac);
            c.Work *= frac;
            c.Entry.AddTerm("gap_share", frac);
        }
        return freed;
    }

    private static ResourceKey FlowGood(SupplyCand c, List<SupplyDemand> demands)
    {
        for (var i = 0; i < demands.Count; i++)
        {
            if (c.Full.Matched[i] && demands[i].Flow)
            {
                return demands[i].Good;
            }
        }
        return new ResourceKey();
    }

    /// <summary>
    /// The delivery a closed candidate is judged against: a source that does not run out is needed until the
    /// flow stands without the ones that do, so the colony does not lean on a herd it will exhaust while a field
    /// could be planted; a finite source is needed only until the whole flow is covered.
    /// </summary>
    private static double Covered(SupplyCand c, double delivered, double finite) => c.Finite ? delivered : delivered - finite;

    /// <summary>The fraction of the candidate's declared ingredient draws the usable stock still covers, 1 for a candidate that declares none.</summary>
    private static double Affordable(SupplyCand c, Dictionary<ResourceKey, long> usable)
    {
        var scale = 1.0;
        foreach (var q in c.C.UpfrontCost.Resources)
        {
            if (q.Count > 0)
            {
                usable.TryGetValue(q.Key, out var have);
                scale = Math.Min(scale, (double)have / q.Count);
            }
        }
        return scale;
    }
}
