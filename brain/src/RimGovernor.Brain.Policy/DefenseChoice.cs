using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>What a day of an unprotected raid approach costs the colony: the raid harm per day in capability-days, or the fact that leaves it unread.</summary>
public abstract record DefenseHarm
{
    private DefenseHarm() { }

    public sealed record Read(double LossPerDay) : DefenseHarm;

    public sealed record Unread(string Fact) : DefenseHarm;
}

/// <summary>A compared alternative's price over the horizon in capability-days, or why it has none: excluded for a known reason, or unassessed naming the fact.</summary>
public abstract record DefensePrice
{
    private DefensePrice() { }

    public sealed record Assessed(double Days) : DefensePrice;

    public sealed record Excluded(string Reason) : DefensePrice;

    public sealed record Unassessed(string Fact) : DefensePrice;
}

/// <summary>One alternative to compare: the candidate, its protection with its pieces placed, and the protection of what of it already stands (null for one that places nothing).</summary>
public sealed record DefenseAlternative(DefenseCandidate Candidate, DefenseProtection Protection, DefenseProtection? Standing);

/// <summary>What the comparison reads beside the alternatives: the raid harm, the horizon, the free builders, the material budget, the material values, the standing buildings, the trips and the power network.</summary>
public sealed record DefenseChoiceInputs
{
    public DefenseHarm Harm { get; init; } = new DefenseHarm.Unread(DefenseChoiceRules.HarmUnread);

    public double HorizonDays { get; init; } = ColonyModelPolicy.DefaultHorizonDays;

    /// <summary>The colonists able to build that no running Method claims, or the fact that leaves the claims unread.</summary>
    public DefenseTerm Builders { get; init; } = new DefenseTerm.Unassessed(DefenseChoiceRules.WorkClaimsUnread);

    /// <summary>The stock net of every other Method's material claims (WorkResourceView.Materials); null while the work view is unread.</summary>
    public MaterialAvailability? Materials { get; init; }

    public IReadOnlyDictionary<Resource, ConstructionMaterialValue> Values { get; init; } = new Dictionary<Resource, ConstructionMaterialValue>();

    /// <summary>The observed standing building on each cell (its definition), from the construction census.</summary>
    public IReadOnlyDictionary<Cell, string> Standing { get; init; } = new Dictionary<Cell, string>();

    /// <summary>The civilian trips a day along each named route; a lengthened route it lacks is unassessed.</summary>
    public IReadOnlyDictionary<string, double> Trips { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    public PowerTopology Power { get; init; } = new();

    public IReadOnlyDictionary<string, PowerSourceProfile> Sources { get; init; } = new Dictionary<string, PowerSourceProfile>();

    public PowerBattery Battery { get; init; }
}

/// <summary>
/// One alternative with every term of its price: what its unbuilt pieces cost (work over a day's ticks plus materials at their
/// values), the lead its free builders take, the route walking it adds, the share of the day its turrets' network leaves them
/// unpowered (6.4's UnservedShare), the protection lost while it is built (removing standing pieces drops the baseline's), the
/// standing pieces it replaces, and its total price.
/// </summary>
public sealed record DefenseChoiceRow
{
    public DefenseAlternative Alternative { get; init; } = new(new(), new(), null);

    public DefenseTerm Construction { get; init; } = new DefenseTerm.Assessed(0);

    public DefenseTerm LeadDays { get; init; } = new DefenseTerm.Assessed(0);

    public DefenseTerm Routes { get; init; } = new DefenseTerm.Assessed(0);

    public DefenseTerm Interruption { get; init; } = new DefenseTerm.Assessed(0);

    public DefenseTerm Downtime { get; init; } = new DefenseTerm.Assessed(0);

    /// <summary>The standing pieces its structures replace, <c>&lt;def&gt;@x,z</c>.</summary>
    public IReadOnlyList<string> Removes { get; init; } = [];

    /// <summary>Its pieces that do not stand yet, in structure order.</summary>
    public IReadOnlyList<DefenseStructure> Unbuilt { get; init; } = [];

    public DefensePrice Price { get; init; } = new DefensePrice.Assessed(0);

    public string Key => Alternative.Candidate.Key;
}

/// <summary>
/// The comparison's pick: every row, the chosen one, why it won, and the protection the chosen alternative still owes: its
/// approach coverage less what of it the standing structures already give (zero for Retain, and once its pieces stand and draw).
/// </summary>
public sealed record DefenseDecision(IReadOnlyList<DefenseChoiceRow> Rows, DefenseChoiceRow Chosen, string Reason, double Deficit)
{
    public DefenseCandidateKind Kind => Chosen.Alternative.Candidate.Kind;
}

/// <summary>
/// Compares complete defense alternatives against retaining the plan's defense (task 7.3, RG-HABITAT-009) in the shape of the
/// utility and servicing comparisons: Retain is the baseline and keeps ties; an alternative is priced over the horizon as
/// <c>C + R + (1 − S_w)·L·T + (1 − S·(1 − u))·L·(H − T)</c> against Retain's <c>(1 − S_b)·L·H</c>, where L is the raid harm a
/// day, S the share of the approach the alternative covers, S_b Retain's, S_w the share held while it is built (S_b, or none
/// when it replaces standing pieces), T its lead over the free builders, C its unbuilt pieces, R the walking it adds and u the
/// unserved share of its turrets' network. An alternative that leaves unassessed a protection term the baseline assessed, or
/// exposes more civilians, cannot win; an unassessed baseline is retained naming the fact.
/// </summary>
public static class DefenseChoiceRules
{
    private const double Tolerance = 1e-9;

    public const string HarmUnread = "raid_harm:unread";

    /// <summary>The review that derives the harm carried no coupled context.</summary>
    public const string CoupledContextUnread = "raid_harm:coupled_context";

    public const string WorkClaimsUnread = "builders:work_claims";

    public const string NoBuilders = "builders:none_free";

    public const string MaterialsUnread = "materials:work_view";

    /// <summary>The mining an alternative needs first (the killbox's dig) is not priced.</summary>
    public const string DigUnpriced = "dig:mining_unpriced";

    /// <summary>The suffix of the probe row that reads what of a candidate already stands: the same firing cells and approach, no pieces placed.</summary>
    public const string StandingSuffix = "~standing";

    /// <summary>The candidates whose standing protection is probed beside their own: every one not excluded that places pieces, its pieces left out.</summary>
    public static IReadOnlyList<DefenseCandidate> Shadows(IReadOnlyList<DefenseCandidate> candidates) =>
        [.. candidates.Where(c => c.Standing is not DefenseCandidateStanding.Excluded && c.Structures.Count > 0)
            .Select(c => c with { Key = c.Key + StandingSuffix, Structures = [] })];

    /// <summary>Each candidate paired with its protection and its shadow's (Shadows), in candidate order.</summary>
    public static IReadOnlyList<DefenseAlternative> Alternatives(IReadOnlyList<DefenseCandidate> candidates, IReadOnlyList<DefenseProtection> protections)
    {
        var by = protections.ToDictionary(p => p.Candidate, StringComparer.Ordinal);
        return [.. candidates.Select(c => new DefenseAlternative(c, by.GetValueOrDefault(c.Key) ?? new DefenseProtection { Candidate = c.Key },
            c.Structures.Count == 0 ? null : by.GetValueOrDefault(c.Key + StandingSuffix)))];
    }

    /// <summary>The colonists able to build (LayoutClusterRules.BuildersOf) that no running Method claims.</summary>
    public static int FreeBuilders(WorkPawnsRead pawns, IReadOnlyCollection<string> claimed) =>
        LayoutClusterRules.BuildersOf(pawns, pawn => !claimed.Contains(pawn));

    /// <summary>
    /// The raid harm a day: unread naming <c>threat_timing:&lt;finding&gt;</c> while the threat schedule is not assessed, else the
    /// review's coupled forecast's raid loss a day over the horizon (<see cref="HarmOf(ForecastCoupledContext?)"/>). The review
    /// derives it on its completed facts (ColonyReview.RaidHarm); the defense step reads that.
    /// </summary>
    public static DefenseHarm HarmOf(ReviewFacts f)
    {
        try
        {
            var schedule = ThreatSchedule.Of(f.ThreatTable, f.Census.Threat.Observed);
            if (!schedule.Assessed)
            {
                return new DefenseHarm.Unread("threat_timing:" + (schedule.Findings.Count > 0 ? schedule.Findings[0] : "unassessed"));
            }
        }
        catch (RequiredFactException e)
        {
            return new DefenseHarm.Unread("threat_timing:" + e.Fact);
        }
        return HarmOf(f.CoupledAlternatives?.Context);
    }

    /// <summary>The coupled forecast's raid loss a day over the horizon (ForecastPractice.LossPerDay at the observed defense); <c>raid_harm:coupled_context</c> without one, <c>raid_harm:&lt;fact&gt;</c> while its harm inputs are unread.</summary>
    public static DefenseHarm HarmOf(ForecastCoupledContext? context)
    {
        if (context is null)
        {
            return new DefenseHarm.Unread(CoupledContextUnread);
        }
        try
        {
            return new DefenseHarm.Read(ForecastPractice.LossPerDay(context, new ForecastSupportConsequences(), ColonyModelPolicy.DefaultHorizonDays * GameTime.TicksPerDay, 0));
        }
        catch (RequiredFactException e)
        {
            return new DefenseHarm.Unread("raid_harm:" + e.Fact);
        }
    }

    /// <summary>The observed standing buildings by cell: each cell of each building's footprint to its definition.</summary>
    public static IReadOnlyDictionary<Cell, string> StandingOf(ReviewFacts f)
    {
        var standing = new Dictionary<Cell, string>();
        foreach (var b in f.CurrentConstruction.Buildings)
        {
            foreach (var c in b.Cells)
            {
                standing.TryAdd(c, b.Building.Definition);
            }
        }
        return standing;
    }
    /// <summary>
    /// The comparison's inputs from a reading's facts: the review's <paramref name="harm"/>, the free builders, material budget,
    /// standing buildings, power network and <paramref name="plan"/>'s trips a day (TripsOf), at <paramref name="values"/>.
    /// </summary>
    public static DefenseChoiceInputs InputsOf(ReviewFacts f, LayoutPlan plan, DefenseHarm harm, IReadOnlyDictionary<Resource, ConstructionMaterialValue> values)
    {
        DefenseTerm builders;
        if (f.WorkView is not { } view)
        {
            builders = new DefenseTerm.Unassessed(WorkClaimsUnread);
        }
        else
        {
            try
            {
                builders = new DefenseTerm.Assessed(FreeBuilders(f.ForecastObserved.WorkPawns, view.Claims.Pawns.ToHashSet(StringComparer.Ordinal)));
            }
            catch (RequiredFactException e)
            {
                builders = new DefenseTerm.Unassessed("builders:" + e.Fact);
            }
        }
        var standing = StandingOf(f);
        return new DefenseChoiceInputs
        {
            Harm = harm,
            Builders = builders,
            Materials = f.WorkView?.Materials,
            Values = values,
            Standing = standing,
            Power = f.EssentialInputs.Power,
            Sources = f.PowerSources,
            Battery = f.PowerBattery,
            Trips = TripsOf(plan),
        };
    }

    /// <summary>
    /// The walks a day along each route the candidates name, from the tiled plan's trip frequencies (LayoutPlan.Trips between
    /// its rooms, OutsideTrips to the outside): a role pair's trips between rooms of those roles, an entrance pair's the outside
    /// trips of the other role's rooms, and <see cref="DefenseCandidateRules.RouteMapEdge"/> every outside trip. A trip weight is
    /// one way and a visit walks there and back (LayoutTileAirlock's convention), so each counts twice. A plan with no trip
    /// frequencies (untiled) gives none, and the field trips no plan carries stay unread.
    /// </summary>
    public static IReadOnlyDictionary<string, double> TripsOf(LayoutPlan plan)
    {
        var output = new Dictionary<string, double>(StringComparer.Ordinal);
        if (plan.Trips.Count == 0 && plan.OutsideTrips.Count == 0)
        {
            return output;
        }
        var roles = new Dictionary<int, PlannedRole>();
        foreach (var r in plan.Rooms)
        {
            roles.TryAdd(r.Id, r.Role);
        }
        foreach (var trip in LayoutRouteRules.RouteTrips)
        {
            double w = 0;
            if (trip.From == default || trip.To == default)
            {
                var other = trip.From == default ? trip.To : trip.From;
                w = plan.OutsideTrips.Where(t => roles.GetValueOrDefault(t.Room) == other).Sum(t => t.Weight);
            }
            else
            {
                foreach (var t in plan.Trips)
                {
                    var (a, b) = (roles.GetValueOrDefault(t.A), roles.GetValueOrDefault(t.B));
                    if ((a == trip.From && b == trip.To) || (a == trip.To && b == trip.From))
                    {
                        w += t.Weight;
                    }
                }
            }
            var name = DefenseCandidateRules.RouteName(trip.From, trip.To);
            output[name] = output.GetValueOrDefault(name) + (2 * w);
        }
        output[DefenseCandidateRules.RouteMapEdge] = 2 * plan.OutsideTrips.Sum(t => t.Weight);
        return output;
    }

    /// <summary>The materials the alternatives' pieces cost, for the material values read.</summary>
    public static IReadOnlyList<Resource> Materials(IEnumerable<DefenseCandidate> candidates) =>
        [.. candidates.SelectMany(c => c.Structures).Select(s => s.Piece).OfType<DefensePiece.Priced>().SelectMany(p => p.Costs.Select(a => a.Resource)).Distinct()];

    private static double? Value(DefenseTerm t) => t is DefenseTerm.Assessed a ? a.Value : null;

    private static string Fact(DefenseTerm t) => ((DefenseTerm.Unassessed)t).Fact;

    /// <summary>
    /// Whether <paramref name="row"/> orders ahead of <paramref name="best"/> at an equal assessed price: more shots, then more
    /// cover. Firepower and cover only order equal prices and are otherwise unpriced, because no fact converts them to loss
    /// stopped (the model has no fight); a still-equal row keeps the earlier one (the generator's order).
    /// </summary>
    private static bool Orders(DefenseChoiceRow row, DefenseChoiceRow best)
    {
        var (rs, bs) = (Value(row.Alternative.Protection.Shots) ?? 0, Value(best.Alternative.Protection.Shots) ?? 0);
        if (Math.Abs(rs - bs) > Tolerance)
        {
            return rs > bs;
        }
        return (Value(row.Alternative.Protection.Cover) ?? 0) > (Value(best.Alternative.Protection.Cover) ?? 0) + Tolerance;
    }

    /// <summary>Picks among <paramref name="alternatives"/> (Retain among them, the baseline) at <paramref name="inputs"/>.</summary>
    public static DefenseDecision Choose(IReadOnlyList<DefenseAlternative> alternatives, DefenseChoiceInputs inputs)
    {
        if (!double.IsFinite(inputs.HorizonDays) || inputs.HorizonDays <= 0)
        {
            throw new DomainException("defense horizon must be positive");
        }
        var baseline = alternatives.SingleOrDefault(a => a.Candidate.Kind == DefenseCandidateKind.Retain)
            ?? throw new DomainException("retaining the plan's defense is the comparison's own baseline");
        var retain = Retained(baseline, inputs);
        var basePrice = retain.Price;
        var rows = new List<DefenseChoiceRow>();
        var best = retain;
        foreach (var a in alternatives)
        {
            if (ReferenceEquals(a, baseline))
            {
                rows.Add(retain);
                continue;
            }
            var row = Priced(a, baseline, inputs);
            rows.Add(row);
            if (basePrice is DefensePrice.Assessed { Days: var floor } && row.Price is DefensePrice.Assessed { Days: var p }
                && best.Price is DefensePrice.Assessed { Days: var current }
                && (p < Math.Min(floor, current) - Tolerance || (p < floor - Tolerance && Math.Abs(p - current) <= Tolerance && Orders(row, best))))
            {
                best = row;
            }
        }
        var reason = !ReferenceEquals(best, retain) ? best.Key
            : basePrice is DefensePrice.Unassessed u ? u.Fact
            : "retain below every alternative";
        return new DefenseDecision(rows, best, reason, Deficit(best));
    }

    /// <summary>
    /// The baseline's row: <c>(1 − S)·L·H</c> while everything it retains stands. A bound choice (DefenseBindingRules) whose
    /// pieces are not all built is priced like the alternative it was: <c>C + R + (1 − S_s)·L·T + (1 − S·(1 − u))·L·(H − T)</c>, S_s
    /// what of it stands, so it is kept while it is still the cheapest and re-selected only when fresh facts make another
    /// cheaper. A term it cannot assess leaves it retained naming the fact; it is never excluded.
    /// </summary>
    private static DefenseChoiceRow Retained(DefenseAlternative baseline, DefenseChoiceInputs inputs)
    {
        var h = inputs.HorizonDays;
        var row = new DefenseChoiceRow { Alternative = baseline };
        var sb = Value(baseline.Protection.Covered);
        if (sb is null)
        {
            return row with { Price = new DefensePrice.Unassessed(Fact(baseline.Protection.Covered)) };
        }
        if (inputs.Harm is not DefenseHarm.Read harm)
        {
            return row with { Price = new DefensePrice.Unassessed(((DefenseHarm.Unread)inputs.Harm).Fact) };
        }
        var unbuilt = baseline.Candidate.Structures.Where(s => inputs.Standing.GetValueOrDefault(s.Cell) != s.Definition).ToList();
        if (unbuilt.Count == 0)
        {
            return row with { Price = new DefensePrice.Assessed((1 - sb.Value) * harm.LossPerDay * h) };
        }
        var removes = Removals(unbuilt, inputs);
        var (construction, lead, _) = Construction(unbuilt, inputs);
        var routes = Routes(baseline.Candidate, inputs);
        var interruption = Interruption(baseline.Protection, inputs);
        row = row with { Unbuilt = unbuilt, Removes = removes, Construction = construction, LeadDays = lead, Routes = routes, Interruption = interruption };
        var stood = baseline.Standing is { } s ? s.Covered : new DefenseTerm.Unassessed(DefenseBindingRules.RemovalHeld + ":replacement_unprobed");
        foreach (var term in new[] { construction, lead, routes, interruption, stood })
        {
            if (term is DefenseTerm.Unassessed tu)
            {
                return row with { Price = new DefensePrice.Unassessed(tu.Fact) };
            }
        }
        var l = harm.LossPerDay;
        var t = Math.Min(h, Value(lead)!.Value);
        var sa = sb.Value * (1 - Value(interruption)!.Value);
        return row with { Price = new DefensePrice.Assessed(Value(construction)!.Value + Value(routes)!.Value + ((1 - Value(stood)!.Value) * l * t) + ((1 - sa) * l * (h - t))) };
    }

    /// <summary>The standing buildings the unbuilt pieces replace, <c>&lt;def&gt;@x,z</c>, each once.</summary>
    private static List<string> Removals(IEnumerable<DefenseStructure> unbuilt, DefenseChoiceInputs inputs) =>
        [.. unbuilt.Where(s => inputs.Standing.ContainsKey(s.Cell)).Select(s => string.Create(CultureInfo.InvariantCulture, $"{inputs.Standing[s.Cell]}@{s.Cell.X},{s.Cell.Z}"))
            .Distinct(StringComparer.Ordinal)];

    /// <summary>The coverage the baseline gives now: what of it stands when it places pieces (all of it while that is unprobed), else its coverage.</summary>
    private static double? Observed(DefenseAlternative baseline) =>
        baseline.Standing is { Covered: DefenseTerm.Assessed s } ? s.Value : Value(baseline.Protection.Covered);

    /// <summary>The coverage the chosen alternative owes beyond what of it stands: none for one that places nothing; all of it while its standing protection is unread.</summary>
    private static double Deficit(DefenseChoiceRow chosen)
    {
        var a = chosen.Alternative;
        if (a.Candidate.Structures.Count == 0 || Value(a.Protection.Covered) is not { } full)
        {
            return 0;
        }
        return a.Standing is { } s && Value(s.Covered) is { } stood ? Math.Max(0, full - stood) : full;
    }

    private static DefenseChoiceRow Priced(DefenseAlternative a, DefenseAlternative baseline, DefenseChoiceInputs inputs)
    {
        var c = a.Candidate;
        var row = new DefenseChoiceRow { Alternative = a };
        if (c.Standing is DefenseCandidateStanding.Excluded x)
        {
            return row with { Price = new DefensePrice.Excluded(x.Reason) };
        }
        if (c.Standing is DefenseCandidateStanding.Unassessed u0)
        {
            return row with { Price = new DefensePrice.Unassessed(u0.Fact) };
        }
        var mine = a.Protection;
        var theirs = baseline.Protection;
        foreach (var (m, b) in new[] { (mine.Covered, theirs.Covered), (mine.Staffing, theirs.Staffing), (mine.Ammunition, theirs.Ammunition) })
        {
            if (m is DefenseTerm.Unassessed um && b is DefenseTerm.Assessed)
            {
                return row with { Price = new DefensePrice.Unassessed(um.Fact) };
            }
        }
        if (Value(mine.Exposure) is { } exposed && Value(theirs.Exposure) is { } held && exposed > held)
        {
            return row with { Price = new DefensePrice.Excluded(string.Create(CultureInfo.InvariantCulture, $"civilians_exposed:{exposed}>{held}")) };
        }

        if (c.Dig.Count > 0)
        {
            // The rock the killbox opening mines first has no work price on the catalog read.
            return row with { Price = new DefensePrice.Unassessed(string.Create(CultureInfo.InvariantCulture, $"{DigUnpriced}:{c.Dig.Count}")) };
        }
        var unbuilt = c.Structures.Where(s => inputs.Standing.GetValueOrDefault(s.Cell) != s.Definition).ToList();
        var removes = Removals(unbuilt, inputs);
        row = row with { Unbuilt = unbuilt, Removes = removes };
        var (construction, lead, why) = Construction(unbuilt, inputs);
        var routes = Routes(c, inputs);
        var interruption = Interruption(mine, inputs);
        row = row with { Construction = construction, LeadDays = lead, Routes = routes, Interruption = interruption };
        var h = inputs.HorizonDays;
        // What holds while this one is built: the baseline as it stands now (a bound choice only what of it is built).
        var sb = Observed(baseline);
        var held0 = removes.Count > 0 ? 0 : sb;
        DefenseTerm downtime = inputs.Harm is DefenseHarm.Read r0 && sb is { } b0 && Value(lead) is { } t0
            ? new DefenseTerm.Assessed((b0 - (held0 ?? 0)) * r0.LossPerDay * Math.Min(h, t0))
            : new DefenseTerm.Unassessed(inputs.Harm is DefenseHarm.Unread hu ? hu.Fact : lead is DefenseTerm.Unassessed lu ? lu.Fact : Fact(theirs.Covered));
        row = row with { Downtime = downtime };
        if (why is not null)
        {
            return row with { Price = why };
        }
        foreach (var term in new[] { construction, lead, routes, interruption, mine.Covered })
        {
            if (term is DefenseTerm.Unassessed tu)
            {
                return row with { Price = new DefensePrice.Unassessed(tu.Fact) };
            }
        }
        if (inputs.Harm is not DefenseHarm.Read harm || sb is null || held0 is not { } sw)
        {
            return row with { Price = new DefensePrice.Unassessed(Fact(downtime)) };
        }
        var l = harm.LossPerDay;
        var t = Math.Min(h, Value(lead)!.Value);
        var sa = Value(mine.Covered)!.Value * (1 - Value(interruption)!.Value);
        var price = Value(construction)!.Value + Value(routes)!.Value + ((1 - sw) * l * t) + ((1 - sa) * l * (h - t));
        return row with { Price = new DefensePrice.Assessed(price) };
    }

    /// <summary>
    /// The unbuilt pieces' price (LayoutClusterRules.PiecePrice: work over a day's ticks plus each material at its value) and lead
    /// (ProspectiveCapacityRules.PieceOption: the work over the free builders); an unpriced piece, an unvalued material or unread
    /// claims leave them unassessed, a material the stock net of other claims lacks or no free builder excludes the alternative.
    /// </summary>
    private static (DefenseTerm Construction, DefenseTerm Lead, DefensePrice? Excluded) Construction(List<DefenseStructure> unbuilt, DefenseChoiceInputs inputs)
    {
        if (unbuilt.Count == 0)
        {
            return (new DefenseTerm.Assessed(0), new DefenseTerm.Assessed(0), null);
        }
        var work = 0.0;
        var costs = new Dictionary<Resource, long>();
        foreach (var s in unbuilt)
        {
            if (s.Piece is not DefensePiece.Priced p)
            {
                var fact = ((DefensePiece.Unpriced)s.Piece).Fact;
                return (new DefenseTerm.Unassessed(fact), new DefenseTerm.Unassessed(fact), null);
            }
            work += p.Work;
            foreach (var a in p.Costs)
            {
                costs[a.Resource] = costs.GetValueOrDefault(a.Resource) + a.Count;
            }
        }
        var list = costs.OrderBy(kv => kv.Key.Value, StringComparer.Ordinal).Select(kv => new Amount(kv.Key, kv.Value)).ToList();
        if (list.FirstOrDefault(a => !inputs.Values.ContainsKey(a.Resource)) is { } unvalued)
        {
            var fact = "material_value:" + unvalued.Resource.Value;
            return (new DefenseTerm.Unassessed(fact), new DefenseTerm.Unassessed(fact), null);
        }
        var price = LayoutClusterRules.PiecePrice((work, list), inputs.Values);
        DefensePrice? excluded = null;
        switch (inputs.Materials)
        {
            case null:
                return (new DefenseTerm.Unassessed(MaterialsUnread), new DefenseTerm.Unassessed(MaterialsUnread), null);
            case MaterialAvailability.Blocked b:
                var blocked = "materials:" + b.Reason;
                return (new DefenseTerm.Unassessed(blocked), new DefenseTerm.Unassessed(blocked), null);
            case MaterialAvailability.Assessed { Balances: var balances }:
                foreach (var a in list)
                {
                    var units = balances.GetValueOrDefault(a.Resource) ?? new MaterialUnits.Exact(0);
                    if (units is MaterialUnits.Open)
                    {
                        var open = "material_claim_open:" + a.Resource.Value;
                        return (new DefenseTerm.Unassessed(open), new DefenseTerm.Unassessed(open), null);
                    }
                    if (units is MaterialUnits.Exact { Count: var have } && have < a.Count)
                    {
                        excluded ??= new DefensePrice.Excluded("materials:" + a.Resource.Value);
                    }
                }
                break;
        }
        if (inputs.Builders is DefenseTerm.Unassessed bu)
        {
            return (new DefenseTerm.Assessed(price), new DefenseTerm.Unassessed(bu.Fact), excluded);
        }
        var builders = (int)Value(inputs.Builders)!.Value;
        if (ProspectiveCapacityRules.PieceOption("defense", work, price, builders) is not { } option)
        {
            return (new DefenseTerm.Assessed(price), new DefenseTerm.Unassessed(NoBuilders), excluded ?? new DefensePrice.Excluded(NoBuilders));
        }
        return (new DefenseTerm.Assessed(option.CostDays), new DefenseTerm.Assessed(option.LeadDays), excluded);
    }

    /// <summary>The walking a day the lengthened trips add over the horizon, in capability-days; an unprobed trip or one whose trips a day are unread is unassessed.</summary>
    private static DefenseTerm Routes(DefenseCandidate c, DefenseChoiceInputs inputs)
    {
        var days = 0.0;
        foreach (var r in c.Routes)
        {
            switch (r.Effect)
            {
                case DefenseRouteEffect.Unprobed:
                    return new DefenseTerm.Unassessed(r.Route + ":unprobed");
                case DefenseRouteEffect.Longer { AddedCells: var cells }:
                    if (!inputs.Trips.TryGetValue(r.Route, out var trips))
                    {
                        return new DefenseTerm.Unassessed("route_trips:" + r.Route);
                    }
                    days += cells * FieldPlacementRules.WalkTicksPerCell * trips / GameTime.TicksPerDay * inputs.HorizonDays;
                    break;
                case DefenseRouteEffect.Closed:
                    return new DefenseTerm.Unassessed(r.Route + ":closed");
            }
        }
        return new DefenseTerm.Assessed(days);
    }

    /// <summary>
    /// The largest share of the day a planned turret's network leaves unserved with the turrets' draw added (6.4's
    /// UtilityChoiceRules.UnservedShare over the network's live producers, its storage and its consumers' draw); 0 with no draw.
    /// A live producer whose delivery profile the catalog lacks leaves it unassessed.
    /// </summary>
    private static DefenseTerm Interruption(DefenseProtection p, DefenseChoiceInputs inputs)
    {
        var share = 0.0;
        foreach (var g in p.Draws.GroupBy(d => d.Network, StringComparer.Ordinal))
        {
            var producers = new List<PowerProducer>();
            double storage = 0, demand = g.Sum(d => d.Watts);
            foreach (var b in inputs.Power.Buildings.Where(b => b.Connected && b.Network == g.Key))
            {
                storage += b.StorageWD;
                if (b.BaseW < 0)
                {
                    demand -= b.BaseW;
                }
                else if (b.BaseW > 0 && b.SwitchedOn && !b.Unfueled && !b.BrokenDown)
                {
                    if (!inputs.Sources.TryGetValue(b.Definition, out var profile))
                    {
                        return new DefenseTerm.Unassessed("power_profile:" + b.Definition);
                    }
                    producers.Add(new PowerProducer { NominalW = b.BaseW, Profile = profile });
                }
            }
            share = Math.Max(share, UtilityChoiceRules.UnservedShare(producers, 0, storage, demand, inputs.Battery.Efficiency, inputs.Power.Eclipse));
        }
        return new DefenseTerm.Assessed(share);
    }
}
