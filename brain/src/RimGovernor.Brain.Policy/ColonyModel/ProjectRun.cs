namespace RimGovernor.Brain.Policy;

// The project graph's state during one tier-2 run (#3530 of #3470). Labor allocation v1 is a fixed order with no search: at each
// event the free hours of each pawn (PawnCycle, this twelfth) go to the ready nodes in the graph's priority order, skill gates
// respected, a node taking all the hours of every pawn able to work on it. One pass per event interval; the rates it sets are
// constant until the next event, so each node's finish time is exact and the engine fires its completion event there.
// Growth needs no labor: its rate comes from CropGrowthModel for the twelfth.

/// <summary>The mutable state of the graph's nodes in one <see cref="ColonyModel.Run"/>.</summary>
internal sealed class ProjectRun
{
    private enum Status
    {
        Waiting,
        Done,
        Failed,
    }

    private sealed class Live(ProjectNode node)
    {
        internal ProjectNode Node { get; } = node;

        /// <summary>Work (or, for growth, growth fraction) still to do.</summary>
        internal double Remaining { get; set; } = node.Work;

        internal Status State { get; set; }

        internal bool Started { get; set; }

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
    private double now;

    internal ProjectRun(ColonyModelInput input, ModelPawn[] pawns, PawnCycle[][] cycles, ProjectGraph graph, InjectedProgress? injected, double horizon, SortedSet<string> findings)
    {
        DomainException.Require(graph.Nodes.Select(n => n.Id).Distinct().Count() == graph.Nodes.Count && graph.Nodes.All(n => n.Id.Length > 0), "project ids must be unique and named");
        this.input = input;
        this.pawns = pawns;
        this.cycles = cycles;
        this.horizon = horizon;
        this.findings = findings;
        nodes = [.. graph.Nodes.Select(n => new Live(n))];
        if (injected is not null && nodes.FirstOrDefault(n => n.Node.Id == injected.Node) is { } head)
        {
            head.Remaining = Math.Max(0, head.Remaining - injected.Work);
        }

        foreach (var finding in graph.Findings)
        {
            findings.Add(finding);
        }

        if (input.Stocks.Materials.TryGetValue(out var held))
        {
            foreach (var amount in held)
            {
                ledger[amount.Resource] = ledger.GetValueOrDefault(amount.Resource) + amount.Count;
            }
        }
    }

    /// <summary>Sets every node's rate for the twelfth now entered; <paramref name="boundary"/> is the day it ends.</summary>
    internal void Allocate(double at, int twelfth, double boundary)
    {
        now = at;
        var indoor = new double[pawns.Length];
        var outdoor = new double[pawns.Length];
        for (var p = 0; p < pawns.Length; p++)
        {
            indoor[p] = cycles[p][twelfth].IndoorHours.TryGetValue(out var i) ? i : 0;
            outdoor[p] = cycles[p][twelfth].OutdoorHours.TryGetValue(out var o) ? o : 0;
        }

        var byId = nodes.ToDictionary(n => n.Node.Id, StringComparer.Ordinal);
        foreach (var live in nodes)
        {
            live.Rate = 0;
            if (live.State != Status.Waiting)
            {
                continue;
            }

            var prerequisites = live.Node.Prerequisites.Select(id => byId.GetValueOrDefault(id)).ToList();
            if (prerequisites.Any(p => p is null || p.State == Status.Failed))
            {
                live.State = Status.Failed;
                findings.Add($"project_blocked:{live.Node.Id}");
                continue;
            }

            if (prerequisites.Any(p => p!.State != Status.Done))
            {
                continue;
            }

            if (live.Node.Kind == ProjectKind.Grow)
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
            if (live.State == Status.Waiting && live.Rate > 0)
            {
                yield return (now + (live.Remaining / live.Rate), live.Node.Kind == ProjectKind.Harvest ? ModelEventKind.Harvest : ModelEventKind.ProjectCompletion, live.Node.Id);
            }
        }
    }

    internal void Advance(double span)
    {
        foreach (var live in nodes)
        {
            if (live.State == Status.Waiting && live.Rate > 0)
            {
                live.Remaining = Math.Max(0, live.Remaining - (live.Rate * span));
            }
        }
    }

    /// <summary>The apparel the run's craft nodes finished, in completion order.</summary>
    internal EquatableArray<ApparelMade> Apparel => EquatableArray.From(apparel);

    /// <summary>Completes the node at <paramref name="day"/>, moving the stocks it consumes and makes. <paramref name="levels"/> is indexed by <see cref="ModelStock"/>.</summary>
    internal void Complete(string id, double day, double[] levels)
    {
        var live = nodes.First(n => n.Node.Id == id);
        live.State = Status.Done;
        live.Remaining = 0;
        var node = live.Node;
        switch (node.Kind)
        {
            case ProjectKind.Harvest:
                Harvest(node, levels);
                break;
            case ProjectKind.Craft:
                levels[(int)ModelStock.Materials] -= node.Ingredients.Sum(a => a.Count);
                foreach (var product in node.Products)
                {
                    ledger[product.Resource] = ledger.GetValueOrDefault(product.Resource) + product.Count;
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
            case ProjectKind.Construction:
                levels[(int)ModelStock.Materials] -= node.Ingredients.Sum(a => a.Count);
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
        foreach (var live in nodes.Where(n => n.State != Status.Done))
        {
            findings.Add($"project_incomplete:{live.Node.Id}");
        }

        foreach (var id in unstaffed.Concat(materialsShort))
        {
            if (nodes.First(n => n.Node.Id == id).State == Status.Waiting)
            {
                findings.Add(unstaffed.Contains(id) ? $"project_unstaffed:{id}" : $"project_materials_short:{id}");
            }
        }
    }

    private CropChoice? CropOf(ProjectNode node) =>
        input.Crops.TryGetValue(out var crops) ? crops.FirstOrDefault(c => c.Name == node.Crop) : null;

    private double MeanFertility(ProjectNode node) =>
        input.Land.TryGetValue(out var land) && land.FirstOrDefault(f => f.Id == node.Field) is { } field && field.Fertility.Count > 0 ? field.Fertility.Average() : double.NaN;

    private void Grow(Live live, int twelfth, double boundary)
    {
        var node = live.Node;
        if (CropOf(node) is not { } crop || double.IsNaN(MeanFertility(node)) || !crop.HarvestMinGrowth.TryGetValue(out var target)
            || !input.Climate.TryGetValue(out var climate))
        {
            findings.Add($"crop_growth_unknown:{node.Id}");
            return;
        }

        var mean = MeanFertility(node);
        var daily = new double[climate.Temperatures.Count];
        for (var t = 0; t < daily.Length; t++)
        {
            if (!CropGrowthModel.DailyGrowth(crop, mean, climate.Temperatures[t]).TryGetValue(out daily[t]))
            {
                findings.Add($"crop_growth_unknown:{node.Id}");
                return;
            }
        }

        if (!live.Forecast)
        {
            live.Forecast = true;
            live.Remaining = target;
            if (!CropGrowthModel.Matures(daily, twelfth, boundary - now, target, horizon - now))
            {
                live.State = Status.Failed;
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
        var outside = node.Kind is ProjectKind.Sow or ProjectKind.Harvest;
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

        if (!live.Started)
        {
            if (!Reserve(node))
            {
                materialsShort.Add(node.Id);
                return;
            }

            live.Started = true;
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

    /// <summary>The pawn's work speed on the node, or null when it cannot work on it; every gap is named, never read as permission or as speed 1.</summary>
    private double? Speed(ProjectNode node, CropChoice? crop, ModelPawn pawn)
    {
        switch (node.Kind)
        {
            case ProjectKind.Research:
                if (pawn.ResearchSpeed.TryGetValue(out var research))
                {
                    return research;
                }

                findings.Add($"research_speed_unknown:{pawn.Pawn}");
                return null;
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

                if (pawn.PlantWorkSpeed.TryGetValue(out var plants))
                {
                    return plants;
                }

                findings.Add($"plant_work_speed_unknown:{pawn.Pawn}");
                return null;
            default:
                // The pawn model carries no crafting or construction speed (skill gates of a recipe are not read either).
                findings.Add(node.Kind == ProjectKind.Craft ? "craft_work_speed_unread" : "construction_work_speed_unread");
                return 1;
        }
    }

    /// <summary>The research benches, one researcher each; unread, the researchers are not limited and the gap is named.</summary>
    private int Benches()
    {
        if (input.Research.TryGetValue(out var research) && research.Benches.TryGetValue(out var benches))
        {
            return benches;
        }

        findings.Add("research_bench_count_unread");
        return int.MaxValue;
    }

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

    private void Harvest(ProjectNode node, double[] levels)
    {
        if (CropOf(node) is not { } crop)
        {
            return;
        }

        if (crop.Edible.TryGetValue(out var edible) && edible && crop.HarvestNutrition.TryGetValue(out var nutrition))
        {
            levels[(int)ModelStock.Nutrition] += node.Cells * nutrition;
        }
        else if (crop.Harvests.TryGetValue(out var resource) && crop.UnitsPerCell.TryGetValue(out var units))
        {
            var yield = node.Cells * units;
            levels[(int)ModelStock.Materials] += yield;
            ledger[resource] = ledger.GetValueOrDefault(resource) + yield;
        }
        else
        {
            findings.Add($"crop_yield_unknown:{node.Id}");
        }
    }
}
