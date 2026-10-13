namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Advises a comfort tier for the sleep node and each worked bench. The bed is chosen first against the
    /// planner's own, then each worked node in graph order against the choices so far: a chair is worth what it
    /// adds to the day the bed and the earlier chairs leave, so a bed that already holds the level makes a work
    /// chair worth little. Each choice maximises mood benefit less cost in watts and is the current behaviour
    /// when none beats it; nothing is vetoed. An unknown number leaves every choice unpriced.
    /// </summary>
    public static IReadOnlyList<ComfortChoice> ChooseComfort(ComfortInput input)
    {
        var need = input.Model.ComfortNeed();
        var price = new EnvPrice(input.Mood, input.WorkDayW, input.Pawns, input.ExpectedMood);
        var weights = DefaultSiteTypeWeights();
        double BuildCost(ComfortOption o) => BuildW(weights, o.Build, input.Steel);
        var known = need.Curve.Valid() && input.Pawns > 0 && FinitePositive(input.Steel) && FinitePositive(input.WorkDayW) && input.ExpectedMood >= 0 && input.ExpectedMood <= 1;

        StationNode? sleep = null;
        var work = new List<StationNode>();
        foreach (var n in input.Graph.Nodes)
        {
            if (n.Purpose == StationPurpose.Sleep && n.Floor == 0 && sleep is null)
            {
                sleep = n;
            }
            else if (n.Purpose == StationPurpose.Work && n.DailyWork > 0 && n.Count > 0)
            {
                work.Add(n);
            }
        }
        var output = new List<ComfortChoice>();
        var bedTarget = 0.0;
        var bedKnown = false;
        if (sleep is not null)
        {
            var (baseOption, found) = BedOption(input.Model.Beds, input.Baseline);
            var choice = new ComfortChoice { Node = sleep.Id, Option = baseOption };
            if (known && found)
            {
                IReadOnlyList<EnvSegment> DayOf(ComfortOption o) => EnvDay(o.Comfort + input.Model.BedOffset, []);
                choice = ChooseOption(need, price, choice.Node, DayOf(baseOption), baseOption, UndominatedWith(input.Model.Beds, baseOption), sleep.Count, BuildCost, DayOf);
                bedTarget = choice.Option.Comfort + input.Model.BedOffset;
                bedKnown = true;
            }
            output.Add(choice);
        }
        var spots = new List<EnvSpot>();
        foreach (var n in work)
        {
            var choice = new ComfortChoice { Node = n.Id };
            if (known && bedKnown)
            {
                var hours = n.DailyWork / GameTime.TicksPerHour / input.Pawns;
                var earlier = spots.ToList();
                IReadOnlyList<EnvSegment> DayOf(ComfortOption o) => EnvDay(bedTarget, [.. earlier, new EnvSpot(hours, o.Comfort)]);
                choice = ChooseOption(need, price, n.Id, EnvDay(bedTarget, earlier), new ComfortOption(), Undominated(input.Model.Chairs), n.Count, BuildCost, DayOf);
                if (choice.Option.Def != string.Empty)
                {
                    spots.Add(new EnvSpot(hours, choice.Option.Comfort));
                }
            }
            output.Add(choice);
        }
        return output;
    }

    /// <summary>
    /// The work chairs chosen for the group's worked benches, by bench definition: a priced choice of a chair
    /// over the baseline of none. A node left unpriced, or with no chair chosen, adds none, which leaves the
    /// bench as it was. Empty when no node has one.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> WorkChairsOf(RoomGroup g, IReadOnlyList<ComfortChoice> comfort)
    {
        var output = new Dictionary<string, string>();
        foreach (var s in g.Stations)
        {
            if (s.Purpose != StationPurpose.Work)
            {
                continue;
            }
            foreach (var c in comfort)
            {
                if (c.Node == s.Node && c.Priced && c.Option.Def != string.Empty)
                {
                    output[s.Def] = c.Option.Def;
                }
            }
        }
        return output;
    }

    /// <summary>The undominated options with base kept: the baseline stays a candidate however the rest rank.</summary>
    private static List<ComfortOption> UndominatedWith(IReadOnlyList<ComfortOption> options, ComfortOption baseOption) =>
        Undominated([.. options, baseOption]);

    /// <summary>The baseline bed: the def's option with the most comfort per cost (the planner's own rule), the most comfort when free, by stuff when equal.</summary>
    private static (ComfortOption Option, bool Found) BedOption(IReadOnlyList<ComfortOption> beds, string def)
    {
        var best = new ComfortOption();
        var found = false;
        foreach (var o in beds)
        {
            if (o.Def != def)
            {
                continue;
            }
            if (!found || ComfortRatio(o) > ComfortRatio(best) ||
                (ComfortRatio(o) == ComfortRatio(best) && (o.Comfort > best.Comfort || (o.Comfort == best.Comfort && string.CompareOrdinal(o.Stuff, best.Stuff) < 0))))
            {
                best = o;
                found = true;
            }
        }
        return (best, found);
    }

    private static double ComfortRatio(ComfortOption o) => o.Build <= 0 ? double.PositiveInfinity : o.Comfort / o.Build;

    /// <summary>
    /// Prices each option's day against the base day (the baseline piece's) and returns the one whose mood
    /// benefit less its extra cost over the baseline is largest and positive, count pieces to a node; the
    /// baseline stands otherwise. A loss of mood is a negative benefit and a cheaper piece a saving, so a
    /// cheaper bed can win. An option that cannot be priced leaves the choice unpriced.
    /// </summary>
    private static ComfortChoice ChooseOption(SeekerNeed need, EnvPrice price, string node, IReadOnlyList<EnvSegment> baseDay, ComfortOption baseline, IReadOnlyList<ComfortOption> options, double count, Func<ComfortOption, double> buildW, Func<ComfortOption, IReadOnlyList<EnvSegment>> dayOf)
    {
        var best = new ComfortChoice { Node = node, Option = baseline, Priced = true };
        var bestNet = 0.0;
        var baseCost = buildW(baseline) * count;
        foreach (var o in options)
        {
            if (o == baseline)
            {
                continue;
            }
            var (change, ok) = need.Price(baseDay, dayOf(o), price);
            if (!ok)
            {
                return new ComfortChoice { Node = node, Option = baseline };
            }
            var cost = (buildW(o) * count) - baseCost;
            var net = change.BenefitW - cost;
            if (net > bestNet)
            {
                best = new ComfortChoice { Node = node, Option = o, Priced = true, Gain = change.Gain, BenefitW = change.BenefitW, CostW = cost };
                bestNet = net;
            }
        }
        return best;
    }

    /// <summary>
    /// Prices the comfort tiers over the station graph and the colony's facts: the colonists, a work-day in
    /// watts, steel's value and the colonists' mean mood. Empty while the comfort model or any fact is unknown,
    /// which leaves the furnisher's choice as it is.
    /// </summary>
    internal static IReadOnlyList<ComfortChoice> AdviseComfort(StationFacts f, StationGraph graph, double steel)
    {
        if (!f.Colonists.TryGetValue(out var colonists) || !f.ExpectedMood.TryGetValue(out var mood) || colonists <= 0 || !f.Catalog.Mood.Break.Valid())
        {
            return [];
        }
        return ChooseComfort(new ComfortInput
        {
            Model = f.Catalog.Comfort,
            Mood = f.Catalog.Mood,
            Graph = graph,
            Baseline = f.Furniture.PrimaryBed(),
            Pawns = (int)colonists,
            WorkDayW = StationWorkDayW(f),
            ExpectedMood = mood,
            Steel = steel,
        });
    }
}
