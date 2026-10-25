using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's engine (#3529 of #3470): an event-driven stock-flow simulation. Stocks move at piecewise-constant
// rates; the clock jumps from event to event (twelfth boundary, scheduled project completion or harvest, a stock reaching zero,
// the food runway, the horizon). Pure and deterministic: the same input gives the same events, simultaneous events firing by
// kind then id. Time is days from the review origin, including on continuation. The budget counts events, never wall-clock. Production comes from the project graph (T4);
// until then the only production is ColonyModelPolicy.FixedProduction.

/// <summary>The tier-2 engine.</summary>
public static class ColonyModel
{
    private const int Twelfths = 12;
    private const double Epsilon = 1e-9;

    internal const double TwelfthDays = CalendarRules.YearDays / (double)Twelfths;

    private static readonly string[] StockNames = [nameof(ModelStock.Nutrition), nameof(ModelStock.Materials), nameof(ModelStock.Medicine), nameof(ModelStock.ResearchPoints)];

    /// <summary>
    /// Steps the colony over <see cref="ColonyModelPolicy.HorizonDays"/>. <paramref name="cycles"/> holds one <see cref="PawnCycle.Year"/>
    /// per pawn of the input, in the input's order; each twelfth entered contributes its findings. An input whose other-consumer demand is not a
    /// finite non-negative number runs nothing and says so.
    /// </summary>
    public static ColonyTrajectory Run(ColonyModelInput input, PawnCycle[][] cycles, ColonyModelPolicy policy) =>
        Run(input, ColonyModelState.Observed(input, policy), cycles, policy);

    /// <summary>Runs the same engine from carried state to its common final date. Input calendar and scheduled dates stay review-relative.</summary>
    public static ColonyTrajectory Run(ColonyModelInput input, ColonyModelState start, PawnCycle[][] cycles, ColonyModelPolicy policy)
    {
        DomainException.Require(policy.HorizonDays > 0 && policy.MaxEvents > 0, "invalid colony model policy");
        var findings = new SortedSet<string>(StringComparer.Ordinal) { ColonyTrajectory.MoodUnassessedFinding };
        var pawns = start.Pawns;
        var nutrition = start.Nutrition;
        var climate = input.Climate;
        var otherNutrition = input.OtherNutritionPerDay;
        if (!double.IsFinite(otherNutrition) || otherNutrition < 0)
        {
            findings.Add("colony_model_input_invalid");
            return new ColonyTrajectory { Findings = EquatableArray.From(findings) };
        }

        if (start.Gap(policy.Projects) is { } stateGap)
        {
            findings.Add(stateGap);
            return new ColonyTrajectory { Findings = EquatableArray.From(findings) };
        }

        DomainException.Require(cycles.Length == pawns.Count && cycles.All(c => c.Length == Twelfths), "one twelve-entry cycle per pawn");
        var levels = new double[StockNames.Length];
        levels[(int)ModelStock.Nutrition] = nutrition;
        levels[(int)ModelStock.ResearchPoints] = start.ResearchPoints;
        levels[(int)ModelStock.Materials] = start.Materials.Sum(a => a.Count);
        // Medicine is held by kind, used up best first; the stock is their sum.
        var kinds = start.Medicine.Select(a => a.Resource).ToArray();
        var held = start.Medicine.Select(a => (double)a.Count).ToArray();
        if (policy.Injected is { } injected && Array.FindIndex(kinds, k => ProjectGraph.MedicineKey(k) == injected.Node) is var bumped and >= 0)
        {
            held[bumped] += injected.Work;
        }

        // A crafted kind the colony holds none of joins the kinds after the better ones (#3868): tending takes them first.
        var craft = policy.MedicineCraft;
        if (craft is not null && Array.IndexOf(kinds, craft.Kind) < 0)
        {
            var at = kinds.Count(k => craft.Better.Contains(k));
            kinds = [.. kinds[..at], craft.Kind, .. kinds[at..]];
            held = [.. held[..at], 0, .. held[at..]];
        }

        // So is a bought one (#3907).
        var trade = policy.MedicineTrade;
        if (trade is not null && Array.IndexOf(kinds, trade.Kind) < 0)
        {
            var at = kinds.Count(k => trade.Better.Contains(k));
            kinds = [.. kinds[..at], trade.Kind, .. kinds[at..]];
            held = [.. held[..at], 0, .. held[at..]];
        }

        var crafted = craft is null ? -1 : Array.IndexOf(kinds, craft.Kind);
        var traded = trade is null ? -1 : Array.IndexOf(kinds, trade.Kind);
        var bestHeld = trade is null ? -1 : Array.IndexOf(kinds, trade.Best);
        var tradeOn = trade?.Active ?? false;
        var tradeUnitsLeft = trade?.Units ?? 0;
        var bought = 0.0;
        var unitsLeft = craft?.Units ?? 0;
        levels[(int)ModelStock.Medicine] = held.Sum();
        var startMedicine = EquatableArray.From(held);

        var fallPerDay = new double[pawns.Count];
        for (var i = 0; i < pawns.Count; i++)
        {
            var food = pawns[i].Needs.FirstOrDefault(n => n.Kind == "food");
            if (food is { Fall: >= 0 })
            {
                fallPerDay[i] = food.Fall * GameTime.TicksPerDay;
            }
            else
            {
                findings.Add($"food_fall_unknown:{pawns[i].Pawn}");
            }
        }

        var consumption = fallPerDay.Sum() + otherNutrition;
        var net = new double[StockNames.Length];
        for (var s = 0; s < net.Length; s++)
        {
            net[s] = policy.FixedProduction.Count > s ? policy.FixedProduction[s] : 0;
        }

        net[(int)ModelStock.Nutrition] -= consumption;
        if (policy.MedicineTendsPerDay.TryGetValue(out var tends))
        {
            net[(int)ModelStock.Medicine] -= tends;
        }
        else
        {
            findings.Add("medicine_tend_rate_unknown");
        }

        var medicineBase = net[(int)ModelStock.Medicine];

        var calendar = input.Calendar;
        var calendarKnown = calendar.Valid() && calendar.GrowingYear.Count == CalendarRules.YearDays;
        if (!calendarKnown) findings.Add("food_lead_unknown");
        var graph = new ProjectRun(input, start, [.. pawns], cycles, policy.Projects, policy.Injected, start.EndDay, findings);
        var scheduled = policy.Scheduled.Where(e => start.Day == 0 ? e.Day >= 0 : e.Day > start.Day).OrderBy(e => e.Day).ThenBy(e => e.Kind).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var starved = new List<StarvationSpan>();
        var events = new List<ModelEvent>(Math.Min(policy.MaxEvents, 64));
        var now = start.Day;
        var twelfth = climate.CurrentTwelfth;
        var boundary = Math.Max(climate.TicksToNextTwelfth, 1) / (double)GameTime.TicksPerDay;
        // The carried snapshot is after events at its time. Advance the origin climate without replaying those events.
        while (boundary <= now)
        {
            twelfth = (twelfth + 1) % Twelfths;
            boundary += TwelfthDays;
        }

        var next = 0;
        var runway = default(double?);
        AddCycleFindings(cycles, twelfth, findings);

        while (true)
        {
            if (events.Count >= Math.Min(policy.MaxEvents, 4_096))
            {
                findings.Add(ColonyTrajectory.TruncatedFinding);
                break;
            }

            // One labor pass per event interval: the rates it sets hold until the next event.
            graph.Allocate(now, twelfth, boundary);

            // The earliest candidate; ties go to the lower kind, then the lower id.
            var when = start.EndDay;
            var kind = ModelEventKind.HorizonEnd;
            var id = "";
            var fromGraph = false;
            void Consider(double at, ModelEventKind k, string i, bool project = false)
            {
                at = Math.Max(at, now);
                if (at < when || at == when && (k < kind || k == kind && string.CompareOrdinal(i, id) < 0))
                {
                    (when, kind, id, fromGraph) = (at, k, i, project);
                }
            }

            foreach (var (day, projectKind, projectId) in graph.Due())
            {
                Consider(day, projectKind, projectId, true);
            }

            Consider(boundary, ModelEventKind.TwelfthBoundary, "");
            if (next < scheduled.Length)
            {
                Consider(scheduled[next].Day, scheduled[next].Kind, scheduled[next].Id);
            }

            // The routine trade (#3907): on the medical reserve's latch (below the entry, held to the target, the usable stock without the
            // best kind) the comps' visits bring medicine at their rates until the silver above the reserve is spent.
            var buying = 0.0;
            if (trade is not null)
            {
                tradeOn = Others(held, bestHeld) < trade.Reserve.Threshold(trade.Colonists, tradeOn) - Epsilon;
                if (tradeOn && tradeUnitsLeft > Epsilon)
                {
                    buying = trade.PerDayAt(now);
                }
            }

            var baseRate = medicineBase + buying;

            // The stock-target bill (#3868): below the target the crafter makes medicine at its rate; at it, what tending uses; above
            // it nothing, the stock falling to the target.
            var making = 0.0;
            if (craft is not null && unitsLeft > 0)
            {
                var stocked = levels[(int)ModelStock.Medicine];
                var below = stocked < craft.Target - Epsilon;
                var above = stocked > craft.Target + Epsilon;
                making = below ? craft.PerDay : above ? 0 : Math.Clamp(-baseRate, 0, craft.PerDay);
                if (below && baseRate + making > 0)
                {
                    Consider(now + (craft.Target - stocked) / (baseRate + making), ModelEventKind.MedicineCraft, "target");
                }
                else if (above && baseRate < 0)
                {
                    Consider(now + (stocked - craft.Target) / -baseRate, ModelEventKind.MedicineCraft, "target");
                }

                if (making > 0)
                {
                    Consider(now + unitsLeft / making, ModelEventKind.MedicineCraft, "ingredients");
                }
            }

            net[(int)ModelStock.Medicine] = baseRate + making;

            if (trade is not null)
            {
                foreach (var visits in trade.Sources.Where(s => s.From > now))
                {
                    Consider(visits.From, ModelEventKind.MedicineTrade, "visits");
                }

                if (buying > 0)
                {
                    Consider(now + tradeUnitsLeft / buying, ModelEventKind.MedicineTrade, "silver");
                }

                // Tending takes the best kind first, so the latch's stock (without it) moves with what is bought and crafted into other
                // kinds, and with the tends once the best kind is gone.
                var others = Others(held, bestHeld);
                var best = bestHeld < 0 ? 0 : held[bestHeld];
                var bestNet = bestHeld < 0 ? 0 : (crafted == bestHeld ? making : 0) + (traded == bestHeld ? buying : 0) + medicineBase;
                var othersNet = bestHeld < 0 || best > Epsilon || bestNet > 0 ? net[(int)ModelStock.Medicine] - bestNet : net[(int)ModelStock.Medicine];
                if (best > Epsilon && bestNet < 0)
                {
                    Consider(now + best / -bestNet, ModelEventKind.MedicineTrade, "best");
                }

                if (!tradeOn && othersNet < 0)
                {
                    Consider(now + (others - trade.Reserve.Threshold(trade.Colonists, false)) / -othersNet, ModelEventKind.MedicineTrade, "start");
                }
                else if (tradeOn && othersNet > 0)
                {
                    Consider(now + (trade.Reserve.Threshold(trade.Colonists, true) - others) / othersNet, ModelEventKind.MedicineTrade, "stop");
                }
            }

            for (var s = 0; s < levels.Length; s++)
            {
                if (levels[s] > 0 && net[s] < 0)
                {
                    Consider(now + levels[s] / -net[s], ModelEventKind.StockZero, StockNames[s]);
                }
            }

            // Stocks and the seasonal reserve both move. Scan its native daily segments up to the next
            // physical event, including phase-in knees; a restart uses the same review-relative calendar.
            if (runway is null && calendarKnown && CalendarRunway.Crossing(calendar, now, when,
                levels[(int)ModelStock.Nutrition], net[(int)ModelStock.Nutrition], consumption) is { } crossing)
            {
                Consider(crossing, ModelEventKind.FoodRunway, "");
            }
            var span = when - now;
            if (levels[(int)ModelStock.Nutrition] == 0 && net[(int)ModelStock.Nutrition] < 0 && span > 0)
            {
                if (starved.Count > 0 && starved[^1].End == now)
                {
                    starved[^1] = starved[^1] with { End = when };
                }
                else
                {
                    starved.Add(new StarvationSpan { Start = now, End = when });
                }
            }

            for (var s = 0; s < levels.Length; s++)
            {
                levels[s] = Math.Max(0, levels[s] + net[s] * span);
            }

            if (making > 0)
            {
                held[crafted] += making * span;
                unitsLeft = Math.Max(0, unitsLeft - (making * span));
            }

            if (buying > 0)
            {
                held[traded] += buying * span;
                tradeUnitsLeft = Math.Max(0, tradeUnitsLeft - (buying * span));
                bought += buying * span;
            }

            UseMedicine(held, levels[(int)ModelStock.Medicine]);
            graph.Advance(span);

            now = when;
            switch (kind)
            {
                case ModelEventKind.TwelfthBoundary:
                    twelfth = (twelfth + 1) % Twelfths;
                    boundary += TwelfthDays;
                    AddCycleFindings(cycles, twelfth, findings);
                    break;
                case ModelEventKind.ProjectCompletion:
                case ModelEventKind.Harvest:
                    if (fromGraph)
                    {
                        graph.Complete(id, when, levels);
                        break;
                    }

                    var effect = scheduled[next++];
                    levels[(int)effect.Stock] = Math.Max(0, levels[(int)effect.Stock] + effect.Amount);
                    break;
                case ModelEventKind.StockZero:
                    levels[Array.IndexOf(StockNames, id)] = 0;
                    UseMedicine(held, levels[(int)ModelStock.Medicine]);
                    break;
                case ModelEventKind.FoodRunway:
                    runway = now;
                    break;
                case ModelEventKind.MedicineCraft when id == "ingredients":
                    unitsLeft = 0;
                    break;
                case ModelEventKind.MedicineCraft when craft is not null && levels[(int)ModelStock.Medicine] > craft.Target:
                    levels[(int)ModelStock.Medicine] = craft.Target;
                    UseMedicine(held, craft.Target);
                    break;
                case ModelEventKind.MedicineCraft when craft is not null:
                    held[crafted] += craft.Target - levels[(int)ModelStock.Medicine];
                    levels[(int)ModelStock.Medicine] = craft.Target;
                    break;
                case ModelEventKind.MedicineTrade when id == "silver":
                    tradeUnitsLeft = 0;
                    break;
                case ModelEventKind.MedicineTrade when id == "start":
                    tradeOn = true;
                    break;
                case ModelEventKind.MedicineTrade when id == "stop" && trade is not null:
                    // The stock reached the target the latch holds to: float drift settles on it.
                    var missing = trade.Reserve.Threshold(trade.Colonists, true) - Others(held, bestHeld);
                    if (missing > 0)
                    {
                        held[traded] += missing;
                        levels[(int)ModelStock.Medicine] += missing;
                    }

                    break;
            }

            events.Add(new ModelEvent { Day = now, Kind = kind, Id = id, Levels = EquatableArray.From(levels), Medicine = EquatableArray.From(held) });
            if (kind == ModelEventKind.HorizonEnd)
            {
                break;
            }
        }

        if (trade is not null && bought > trade.Claimed + Epsilon)
        {
            findings.Add(MedicineTrade.LaterBuysFinding);
        }

        graph.Finish();
        return new ColonyTrajectory
        {
            Events = EquatableArray.From(events),
            Starvation = EquatableArray.From(starved),
            Apparel = graph.Apparel,
            Arrivals = graph.Arrivals,
            Skills = graph.Skills,
            MedicineKinds = EquatableArray.From(kinds),
            StartMedicine = startMedicine,
            FoodRunwayDay = runway,
            Findings = EquatableArray.From(findings),
        };
    }

    // The usable medicine the reserve's latch counts (#3907): every kind held but the best.
    private static double Others(double[] held, int best) => held.Sum() - (best < 0 ? 0 : held[best]);

    /// <summary>Brings the kinds to <paramref name="total"/>: what was used up comes off the best kind first; a gain joins the best kind.</summary>
    private static void UseMedicine(double[] held, double total)
    {
        if (held.Length == 0)
        {
            return;
        }

        var used = held.Sum() - total;
        if (used < 0)
        {
            held[0] -= used;
            return;
        }

        for (var k = 0; k < held.Length && used > 0; k++)
        {
            var take = Math.Min(held[k], used);
            held[k] -= take;
            used -= take;
        }
    }

    private static void AddCycleFindings(PawnCycle[][] cycles, int twelfth, SortedSet<string> findings)
    {
        foreach (var year in cycles)
        {
            foreach (var finding in year[twelfth].Findings)
            {
                findings.Add(finding);
            }
        }
    }
}
