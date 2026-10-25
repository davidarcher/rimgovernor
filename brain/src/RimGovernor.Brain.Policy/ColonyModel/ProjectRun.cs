namespace RimGovernor.Brain.Policy;

// The project graph's state during one tier-2 run (#3530 of #3470). Labor allocation v1 is a fixed order with no search: at each
// event the free hours of each pawn (PawnCycle, this twelfth) go to the ready nodes in the graph's priority order, skill gates
// respected, a node taking all the hours of every pawn able to work on it. One pass per event interval; the rates it sets are
// constant until the next event, so each node's finish time is exact and the engine fires its completion event there.
// Growth needs no labor: its rate comes from CropGrowthModel for the twelfth. Each pawn's skills are stocks (SkillStock, #3634) the
// native decay drains.

/// <summary>The mutable state of the graph's nodes in one <see cref="ColonyModel.Run"/>.</summary>
internal sealed class ProjectRun
{
    private sealed class Live(ProjectNode node)
    {
        internal ProjectNode Node { get; } = node;

        /// <summary>Work (or, for growth, growth fraction) still to do.</summary>
        internal double Remaining { get; set; } = node.Work;

        internal ModelProjectStatus State { get; set; }

        internal ModelProjectInputs Inputs { get; set; }

        internal bool Forecast { get; set; }

        /// <summary>Work (or growth) a day until the next event.</summary>
        internal double Rate { get; set; }
    }

    private readonly ColonyModelInput input;
    private readonly ModelPawn[] pawns;
    private readonly PawnCycle[][] cycles;
    private readonly Live[] nodes;
    private readonly double horizon;
    private readonly SortedSet<string> findings;
    private readonly Dictionary<Resource, double> ledger = [];
    private readonly SortedSet<string> unstaffed = new(StringComparer.Ordinal);
    private readonly SortedSet<string> materialsShort = new(StringComparer.Ordinal);
    private readonly List<ApparelMade> apparel = [];
    private readonly List<ResourceArrival> arrivals = [];
    private readonly SkillStock[] stocks;
    private double now;

    internal ProjectRun(ColonyModelInput input, ColonyModelState start, ModelPawn[] pawns, PawnCycle[][] cycles, ProjectGraph graph, InjectedProgress? injected, double horizon, SortedSet<string> findings)
    {
        DomainException.Require(graph.Nodes.Select(n => n.Id).Distinct().Count() == graph.Nodes.Count && graph.Nodes.All(n => n.Id.Length > 0), "project ids must be unique and named");
        this.input = input;
        this.pawns = pawns;
        this.cycles = cycles;
        this.horizon = horizon;
        this.findings = findings;
        nodes = [.. graph.Nodes.Select(n => new Live(n))];
        foreach (var carried in start.Projects)
        {
            var live = nodes.Single(n => n.Node.Id == carried.Id);
            live.Remaining = carried.Remaining;
            live.State = carried.Status;
            live.Inputs = carried.Inputs;
            live.Forecast = carried.GrowthInitialized;
        }

        stocks = [.. pawns.SelectMany(p => SkillStock.Of(p, findings))];
        if (injected is not null && nodes.FirstOrDefault(n => n.Node.Id == injected.Node) is { } head)
        {
            head.Remaining = Math.Max(0, head.Remaining - injected.Work);
        }
        else if (injected is not null && stocks.FirstOrDefault(s => s.Key == injected.Node) is { } stock)
        {
            stock.Raise(start.Day);
        }

        foreach (var finding in graph.Findings)
        {
            findings.Add(finding);
        }

        foreach (var amount in start.Materials)
        {
            ledger[amount.Resource] = ledger.GetValueOrDefault(amount.Resource) + amount.Count;
        }

        // Both kinds of holds stay in the physical aggregate but are unavailable to new reservations.
        foreach (var hold in start.ExternalHolds)
        {
            ledger[hold.Resource] = ledger.GetValueOrDefault(hold.Resource) - hold.Count;
        }
        foreach (var live in nodes.Where(n => n.Inputs == ModelProjectInputs.Held))
        {
            foreach (var amount in live.Node.Ingredients)
            {
                ledger[amount.Resource] = ledger.GetValueOrDefault(amount.Resource) - amount.Count;
            }
        }
    }

    /// <summary>Each skill stock over the run, in pawn then skill order.</summary>
    internal EquatableArray<SkillTrack> Skills => EquatableArray.From(stocks.Select(s => s.Track()));

    /// <summary>Sets every node's rate for the twelfth now entered; <paramref name="boundary"/> is the day it ends.</summary>
    internal void Allocate(double at, int twelfth, double boundary)
    {
        now = at;
        var indoor = new double[pawns.Length];
        var outdoor = new double[pawns.Length];
        for (var p = 0; p < pawns.Length; p++)
        {
            switch (cycles[p][twelfth])
            {
                case PawnCycle.Scheduled day:
                    indoor[p] = day.IndoorHours;
                    outdoor[p] = day.OutdoorHours;
                    break;
                case PawnCycle.NoMealService:
                case PawnCycle.Overbooked:
                    break; // This day has no serviceable labor to allocate.
            }
        }

        var byId = nodes.ToDictionary(n => n.Node.Id, StringComparer.Ordinal);
        foreach (var live in nodes)
        {
            live.Rate = 0;
            if (live.State != ModelProjectStatus.Waiting)
            {
                continue;
            }

            var prerequisites = live.Node.Prerequisites.Select(id => byId.GetValueOrDefault(id)).ToList();
            if (prerequisites.Any(p => p is null || p.State == ModelProjectStatus.Failed))
            {
                live.State = ModelProjectStatus.Failed;
                findings.Add($"project_blocked:{live.Node.Id}");
                continue;
            }

            if (prerequisites.Any(p => p!.State != ModelProjectStatus.Done))
            {
                continue;
            }

            if (live.Node.Kind == ProjectKind.Milestone)
            {
                live.Remaining = 0;
                live.Rate = 1;
            }
            else if (live.Node.Kind == ProjectKind.Knowledge)
            {
                // No study in the run: the project finishes now when its points were given, else it waits.
                live.Rate = live.Remaining <= 0 ? 1 : 0;
            }
            else if (live.Node.Kind == ProjectKind.Grow)
            {
                Grow(live, twelfth, boundary);
            }
            else
            {
                Labor(live, indoor, outdoor);
            }
        }
    }

    /// <summary>The nodes that finish in the current interval: when, the event kind and the id.</summary>
    internal IEnumerable<(double Day, ModelEventKind Kind, string Id)> Due()
    {
        foreach (var live in nodes)
        {
            if (live.State == ModelProjectStatus.Waiting && live.Rate > 0)
            {
                yield return (now + (live.Remaining / live.Rate), live.Node.Kind == ProjectKind.Harvest ? ModelEventKind.Harvest : ModelEventKind.ProjectCompletion, live.Node.Id);
            }
        }
    }

    internal void Advance(double span)
    {
        foreach (var live in nodes)
        {
            if (live.State == ModelProjectStatus.Waiting && live.Rate > 0)
            {
                live.Remaining = Math.Max(0, live.Remaining - (live.Rate * span));
            }
        }

        foreach (var stock in stocks)
        {
            stock.Advance(now, span);
        }
    }

    /// <summary>The apparel the run's craft nodes finished, in completion order.</summary>
    internal EquatableArray<ApparelMade> Apparel => EquatableArray.From(apparel);

    /// <summary>The resources the run's nodes added to the ledger, in completion order.</summary>
    internal EquatableArray<ResourceArrival> Arrivals => EquatableArray.From(arrivals);

    /// <summary>Completes the node at <paramref name="day"/>, moving the stocks it consumes and makes. <paramref name="levels"/> is indexed by <see cref="ModelStock"/>.</summary>
    internal void Complete(string id, double day, double[] levels)
    {
        var live = nodes.First(n => n.Node.Id == id);
        live.State = ModelProjectStatus.Done;
        live.Remaining = 0;
        var node = live.Node;
        switch (node.Kind)
        {
            case ProjectKind.Harvest:
                Harvest(node, day, levels);
                break;
            case ProjectKind.Craft:
                Consume(live, levels);
                foreach (var product in node.Products)
                {
                    Arrive(node, day, product.Resource, product.Count);
                }

                if (node.ProductsToStock)
                {
                    levels[(int)ModelStock.Materials] += node.Products.Sum(a => a.Count);
                }
                else
                {
                    apparel.Add(new ApparelMade { Day = day, Node = node.Id, Products = node.Products, Ingredients = node.Ingredients });
                }

                break;
            case ProjectKind.Hunt:
                levels[(int)ModelStock.Nutrition] += node.Nutrition;
                levels[(int)ModelStock.Materials] += node.Products.Sum(a => a.Count);
                foreach (var product in node.Products)
                {
                    Arrive(node, day, product.Resource, product.Count);
                }

                break;
        }

        for (var s = 0; s < levels.Length; s++)
        {
            levels[s] = Math.Max(0, levels[s]);
        }
    }

    /// <summary>At the end of the run: each node that did not complete, and why where the allocation knows.</summary>
    internal void Finish()
    {
        foreach (var live in nodes.Where(n => n.State != ModelProjectStatus.Done))
        {
            findings.Add($"project_incomplete:{live.Node.Id}");
        }

        foreach (var id in unstaffed.Concat(materialsShort))
        {
            if (nodes.First(n => n.Node.Id == id).State == ModelProjectStatus.Waiting)
            {
                findings.Add(unstaffed.Contains(id) ? $"project_unstaffed:{id}" : $"project_materials_short:{id}");
            }
        }
    }

    private static void Consume(Live live, double[] levels)
    {
        if (live.Inputs != ModelProjectInputs.Consumed)
        {
            levels[(int)ModelStock.Materials] -= live.Node.Ingredients.Sum(a => a.Count);
            live.Inputs = ModelProjectInputs.Consumed;
        }
    }

    private CropChoice? CropOf(ProjectNode node) => input.Crops.FirstOrDefault(c => c.Name == node.Crop);

    private double MeanFertility(ProjectNode node) =>
        input.Land.FirstOrDefault(f => f.Id == node.Field) is { } field && field.Fertility.Count > 0 ? field.Fertility.Average() : double.NaN;

    private void Grow(Live live, int twelfth, double boundary)
    {
        var node = live.Node;
        var climate = input.Climate;
        if (CropOf(node) is not { } crop || double.IsNaN(MeanFertility(node)))
        {
            findings.Add($"crop_growth_unknown:{node.Id}");
            return;
        }

        var target = crop.HarvestMinGrowth;
        var mean = MeanFertility(node);
        var daily = new double[climate.Temperatures.Count];
        for (var t = 0; t < daily.Length; t++)
        {
            daily[t] = CropGrowthModel.DailyGrowth(crop, mean, climate.Temperatures[t]);
        }

        if (!live.Forecast)
        {
            live.Forecast = true;
            live.Remaining = target;
            if (!CropGrowthModel.Matures(daily, twelfth, boundary - now, target, horizon - now))
            {
                live.State = ModelProjectStatus.Failed;
                findings.Add($"crop_will_not_mature:{node.Id}");
                return;
            }
        }

        live.Rate = daily[twelfth];
    }

    private void Labor(Live live, double[] indoor, double[] outdoor)
    {
        var node = live.Node;
        var crop = node.Kind is ProjectKind.Sow or ProjectKind.Harvest ? CropOf(node) : null;
        var outside = node.Kind is ProjectKind.Sow or ProjectKind.Harvest or ProjectKind.Hunt;
        var benches = node.Kind == ProjectKind.Research ? Benches() : int.MaxValue;
        var workers = new List<(int Pawn, double Hours, double Speed)>();
        for (var p = 0; p < pawns.Length; p++)
        {
            var hours = outside ? outdoor[p] : indoor[p] + outdoor[p];
            if (!(hours > 0) || workers.Count >= benches || Speed(node, crop, pawns[p]) is not { } speed)
            {
                continue;
            }

            workers.Add((p, hours, speed));
        }

        if (workers.Count == 0)
        {
            unstaffed.Add(live.Node.Id);
            return;
        }

        if (live.Inputs == ModelProjectInputs.Unpaid)
        {
            if (!Reserve(node))
            {
                materialsShort.Add(node.Id);
                return;
            }

            live.Inputs = ModelProjectInputs.Held;
        }

        unstaffed.Remove(node.Id);
        materialsShort.Remove(node.Id);

        var factor = node.Kind == ProjectKind.Research ? ProjectGraph.ResearchPointsPerWorkTick : 1;
        foreach (var (p, hours, speed) in workers)
        {
            live.Rate += hours * GameTime.TicksPerHour * speed * factor;
            outdoor[p] = 0;
            if (!outside)
            {
                indoor[p] = 0;
            }
        }
    }

    /// <summary>The pawn's work speed on the node, or null when it cannot work on it (a speed of nothing is a pawn who cannot).</summary>
    private double? Speed(ProjectNode node, CropChoice? crop, ModelPawn pawn)
    {
        switch (node.Kind)
        {
            case ProjectKind.Research:
                return pawn.ResearchSpeed > 0 ? pawn.ResearchSpeed : null;
            case ProjectKind.Sow:
            case ProjectKind.Harvest:
                if (crop is null)
                {
                    return null;
                }

                if (node.Kind == ProjectKind.Sow)
                {
                    var (available, finding) = PawnCycle.SowGate(pawn, crop);
                    if (finding.Length > 0)
                    {
                        findings.Add(finding);
                    }

                    if (!available)
                    {
                        return null;
                    }
                }

                return pawn.PlantWorkSpeed > 0 ? pawn.PlantWorkSpeed : null;
            case ProjectKind.Hunt:
                // The work is the hunt planner's pursuit ticks of a hunter, so a pawn who may hunt works it at speed 1.
                return pawn.Hunts ? 1 : null;
            case ProjectKind.Craft:
                // Toils_Recipe.DoRecipeWork: the recipe's workSpeedStat of the crafter, 1 when it names none. The bench's speed
                // factor and the recipe's skill gates are not read on the node (#3868 reads them for the stock arrival).
                findings.Add("craft_bench_speed_and_skill_gate_unmodelled");
                if (node.WorkSpeedStat.Length == 0)
                {
                    return 1;
                }

                if (pawn.WorkSpeeds.FirstOrDefault(s => s.Stat == node.WorkSpeedStat) is { } craft)
                {
                    return craft.Value;
                }

                findings.Add($"craft_work_speed_unknown:{pawn.Pawn}:{node.WorkSpeedStat}");
                return null;
            case ProjectKind.Knowledge:
            case ProjectKind.Grow:
            case ProjectKind.Milestone:
                throw new InvalidOperationException($"a {node.Kind} node takes no labor");
        }

        throw new ArgumentOutOfRangeException(nameof(node), node.Kind, "invalid project kind");
    }

    /// <summary>The research benches, one researcher each.</summary>
    private int Benches() => input.Research.Benches;

    /// <summary>Takes the node's ingredients from the ledger at its start; false while the ledger cannot supply them all.</summary>
    private bool Reserve(ProjectNode node)
    {
        if (node.Ingredients.Any(a => ledger.GetValueOrDefault(a.Resource) < a.Count))
        {
            return false;
        }

        foreach (var a in node.Ingredients)
        {
            ledger[a.Resource] -= a.Count;
        }

        return true;
    }

    /// <summary>Adds <paramref name="count"/> of <paramref name="resource"/> to the ledger on <paramref name="day"/>, recorded as an arrival.</summary>
    private void Arrive(ProjectNode node, double day, Resource resource, double count)
    {
        ledger[resource] = ledger.GetValueOrDefault(resource) + count;
        arrivals.Add(new ResourceArrival { Day = day, Node = node.Id, Resource = resource, Count = count });
    }

    private void Harvest(ProjectNode node, double day, double[] levels)
    {
        if (CropOf(node) is not { } crop)
        {
            return;
        }

        var edible = crop.Edible;

        var nutrition = crop.HarvestNutrition;

        if (edible)
        {
            levels[(int)ModelStock.Nutrition] += node.Cells * nutrition;
        }
        else {
            var yield = node.Cells * crop.UnitsPerCell;
            levels[(int)ModelStock.Materials] += yield;
            Arrive(node, day, crop.Harvests, yield);
        }
    }
}
