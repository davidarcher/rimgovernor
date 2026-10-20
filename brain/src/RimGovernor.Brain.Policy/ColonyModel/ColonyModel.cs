using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's engine (#3529 of #3470): an event-driven stock-flow simulation. Stocks move at piecewise-constant
// rates; the clock jumps from event to event (twelfth boundary, scheduled project completion or harvest, a stock reaching zero,
// the food runway, the horizon). Pure and deterministic: the same input gives the same events, simultaneous events firing by
// kind then id. Time is days from now. The budget counts events, never wall-clock. Production comes from the project graph (T4);
// until then the only production is ColonyModelPolicy.FixedProduction.

/// <summary>The tier-2 engine.</summary>
public static class ColonyModel
{
    private const int Twelfths = 12;

    internal const double TwelfthDays = CalendarRules.YearDays / (double)Twelfths;

    private static readonly string[] StockNames = [nameof(ModelStock.Nutrition), nameof(ModelStock.Materials), nameof(ModelStock.Medicine), nameof(ModelStock.ResearchPoints)];

    /// <summary>
    /// Steps the colony over <see cref="ColonyModelPolicy.HorizonDays"/>. <paramref name="cycles"/> holds one <see cref="PawnCycle.Year"/>
    /// per pawn of the input, in the input's order; each twelfth entered contributes its findings. An input without pawns, nutrition or
    /// a climate runs nothing and says so.
    /// </summary>
    public static ColonyTrajectory Run(ColonyModelInput input, PawnCycle[][] cycles, ColonyModelPolicy policy)
    {
        DomainException.Require(policy.HorizonDays > 0 && policy.MaxEvents > 0, "invalid colony model policy");
        var findings = new SortedSet<string>(StringComparer.Ordinal) { ColonyTrajectory.MoodUnassessedFinding };
        if (!input.Pawns.TryGetValue(out var pawns) || !input.Stocks.Nutrition.TryGetValue(out var nutrition) || !input.Climate.TryGetValue(out var climate))
        {
            findings.Add("colony_model_input_unknown");
            return new ColonyTrajectory { Findings = EquatableArray.From(findings) };
        }

        DomainException.Require(cycles.Length == pawns.Count && cycles.All(c => c.Length == Twelfths), "one twelve-entry cycle per pawn");
        var levels = new double[StockNames.Length];
        levels[(int)ModelStock.Nutrition] = nutrition;
        levels[(int)ModelStock.Materials] = input.Stocks.Materials.TryGetValue(out var materials) ? materials.Sum(a => a.Count) : 0;
        // Medicine is held by kind, used up best first; the stock is their sum.
        var kinds = input.Stocks.Medicine.TryGetValue(out var medicine) ? medicine.Select(a => a.Resource).ToArray() : [];
        var held = input.Stocks.Medicine.TryGetValue(out medicine) ? medicine.Select(a => (double)a.Count).ToArray() : [];
        if (policy.Injected is { } injected && Array.FindIndex(kinds, k => ProjectGraph.MedicineKey(k) == injected.Node) is var bumped and >= 0)
        {
            held[bumped] += injected.Work;
        }

        levels[(int)ModelStock.Medicine] = held.Sum();

        var fallPerDay = new double[pawns.Count];
        for (var i = 0; i < pawns.Count; i++)
        {
            var food = pawns[i].Needs.FirstOrDefault(n => n.Kind == "food");
            if (food is not null && food.Fall.TryGetValue(out var fall) && fall >= 0)
            {
                fallPerDay[i] = fall * GameTime.TicksPerDay;
            }
            else
            {
                findings.Add($"food_fall_unknown:{pawns[i].Pawn}");
            }
        }

        var consumption = fallPerDay.Sum();
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

        var medicineNet = net[(int)ModelStock.Medicine];

        var lead = 0.0;
        if (CalendarRules.HarvestGapDays(input.Calendar, Known(EquatableArray.From(Array.Empty<DisasterCondition>()))).TryGetValue(out var gap))
        {
            lead = gap;
        }
        else
        {
            findings.Add("food_lead_unknown");
        }

        var graph = new ProjectRun(input, [.. pawns], cycles, policy.Projects, policy.Injected, policy.HorizonDays, findings);
        var scheduled = policy.Scheduled.OrderBy(e => e.Day).ThenBy(e => e.Kind).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var starved = new List<StarvationSpan>();
        var events = new List<ModelEvent>(Math.Min(policy.MaxEvents, 64));
        var now = 0.0;
        var twelfth = climate.CurrentTwelfth;
        var boundary = Math.Max(climate.TicksToNextTwelfth, 1) / (double)GameTime.TicksPerDay;
        var next = 0;
        var runway = default(double?);
        AddCycleFindings(cycles, twelfth, findings);

        while (true)
        {
            if (events.Count >= policy.MaxEvents)
            {
                findings.Add(ColonyTrajectory.TruncatedFinding);
                break;
            }

            // One labor pass per event interval: the rates it sets hold until the next event; practice uses up medicine.
            graph.Allocate(now, twelfth, boundary, levels[(int)ModelStock.Medicine]);
            net[(int)ModelStock.Medicine] = medicineNet - graph.MedicineRate;

            // The earliest candidate; ties go to the lower kind, then the lower id.
            var when = policy.HorizonDays;
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

            for (var s = 0; s < levels.Length; s++)
            {
                if (levels[s] > 0 && net[s] < 0)
                {
                    Consider(now + levels[s] / -net[s], ModelEventKind.StockZero, StockNames[s]);
                }
            }

            // The runway is the stock that bridges the lead days at today's consumption.
            var floor = consumption * lead;
            if (runway is null && consumption > 0)
            {
                var level = levels[(int)ModelStock.Nutrition];
                if (level <= floor)
                {
                    Consider(now, ModelEventKind.FoodRunway, "");
                }
                else if (net[(int)ModelStock.Nutrition] < 0)
                {
                    Consider(now + (level - floor) / -net[(int)ModelStock.Nutrition], ModelEventKind.FoodRunway, "");
                }
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
            }

            events.Add(new ModelEvent { Day = now, Kind = kind, Id = id, Levels = EquatableArray.From(levels), Medicine = EquatableArray.From(held) });
            if (kind == ModelEventKind.HorizonEnd)
            {
                break;
            }
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
            FoodRunwayDay = runway,
            Findings = EquatableArray.From(findings),
        };
    }

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
