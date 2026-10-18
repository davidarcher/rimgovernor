namespace RimGovernor.Brain.Policy;

// A player goal as a terminal of the tier-2 project graph (#3505 of #3478): the research, sowing, growth, harvest and crafting
// the goal's demands wait on, built from the nodes #3530 defines. A goal with no path through them is not modelled: it keeps the
// flat value of the ledger (#3503) and an unknown ETA, and the reason is named.

/// <summary>A goal's end in the project graph: the nodes that all complete when the goal is met, and what the goal is worth.</summary>
public sealed record GoalTerminal(string Goal, Importance Importance, int? DeadlineDay, EquatableArray<string> Nodes);

/// <summary>A goal's path: the nodes to add to the model's graph and the terminal; <see cref="Unmodelled"/> says why there is none.</summary>
public sealed record GoalPath(ProjectGraph Graph, GoalTerminal? Terminal, string Unmodelled);

/// <summary>What tier 2 says about one modelled goal.</summary>
public sealed record GoalProjection
{
    /// <summary>Days from now the goal's last node completes in the run as it is; null when the run does not complete it inside the horizon.</summary>
    public double? CompletionDay { get; init; }

    /// <summary>Capability-days a unit of progress on a node adds to the goal (<see cref="ProjectGraph.ProgressKey"/>): the goal's value at its completion day moved earlier by what the unit advances it. Unknown while the run could not be priced.</summary>
    public IReadOnlyDictionary<string, double> PerUnit { get; init; } = new Dictionary<string, double>();

    /// <summary>True when the run was priced, so <see cref="PerUnit"/> replaces the goal's flat per-unit value.</summary>
    public bool Priced { get; init; }
}

/// <summary>Builds goal paths over a model input.</summary>
public static class GoalPaths
{
    /// <summary>The goal's research path: its unfinished projects with their unfinished prerequisites, the goal met when all are done.</summary>
    private static GoalPath Research(GoalRecord goal, GoalsFacts f, ColonyModelInput input)
    {
        var graph = new ProjectGraph();
        foreach (var project in GoalRules.ResearchOf(goal, f))
        {
            graph = graph.Then(ProjectGraph.ResearchChain(input, new ResearchProjectId(project)));
        }

        var nodes = GoalRules.ResearchOf(goal, f).Select(p => ProjectGraph.ResearchId(new ResearchProjectId(p))).Where(id => graph.Nodes.Any(n => n.Id == id)).ToArray();
        return nodes.Length == 0
            ? new(new ProjectGraph(), null, Reason("no_research_node", graph))
            : new(graph, Terminal(goal, f, nodes), "");
    }

    /// <summary>
    /// The path to a worn garment: the research its stuff's plant waits on, sowing, growth and harvest of that crop on a field that has
    /// it or is free, then one craft of the missing garments from the stock and the harvest. Needs a recipe that makes the garment from
    /// the stuff first and, unless the stock covers it, a crop that yields the stuff and a field.
    /// </summary>
    private static GoalPath Worn(GoalRecord goal, GoalsFacts f, ColonyModelInput input)
    {
        var missing = GoalRules.WornMissing(goal, f);
        if (goal.Qualifier == "" || missing is not > 0)
        {
            return Unmodelled(goal.Qualifier == "" ? "stuff_unnamed" : "worn_count_unread");
        }

        var stuff = new Resource(goal.Qualifier);
        if (!input.Recipes.TryGetValue(out var recipes) || !input.Stocks.Materials.TryGetValue(out var materials))
        {
            return Unmodelled("recipes_or_stock_unread");
        }

        var recipe = recipes.FirstOrDefault(r => r.Products.TryGetValue(out var made) && made.Any(p => p.Resource.Value == goal.Target && p.Count > 0)
            && r.Slots.TryGetValue(out var slots) && slots.Count > 0 && slots[0].Count > 0 && slots[0][0].Resource == stuff);
        if (recipe is null)
        {
            return Unmodelled("no_recipe:" + goal.Target);
        }

        var owed = recipe.Slots.Value[0][0].Count * missing.Value;
        var held = materials.Where(a => a.Resource == stuff).Sum(a => a.Count);
        var graph = new ProjectGraph();
        string[] after = [];
        if (held < owed)
        {
            if (!input.Crops.TryGetValue(out var crops) || crops.FirstOrDefault(c => c.Harvests.TryGetValue(out var yields) && yields == stuff) is not { } crop)
            {
                return Unmodelled("no_crop:" + goal.Qualifier);
            }

            if (!input.Land.TryGetValue(out var land) || (land.FirstOrDefault(x => x.Crop == crop.Name) ?? land.FirstOrDefault(x => x.Crop.Length == 0)) is not { } field)
            {
                return Unmodelled("no_field");
            }

            var gates = f.StuffResearch.TryGetValue(goal.Qualifier, out var research) ? research.Select(p => new ResearchProjectId(p)) : [];
            graph = ProjectGraph.CropChain(input, crop.Name, field.Id, EquatableArray.From(gates));
            after = [$"harvest:{field.Id}"];
            if (graph.Nodes.All(n => n.Id != after[0]))
            {
                return Unmodelled(Reason("crop_chain_incomplete", graph));
            }
        }

        var craft = ProjectGraph.Craft(input, recipe.Recipe, EquatableArray.From(after), false, missing.Value);
        graph = graph.Then(craft);
        return craft.Nodes.Count == 0
            ? new(new ProjectGraph(), null, Reason("craft_unread", craft))
            : new(graph, Terminal(goal, f, [craft.Nodes[0].Id]), "");
    }

    /// <summary>The path of <paramref name="goal"/>; a kind with no path through the project graph is unmodelled.</summary>
    public static GoalPath Of(GoalRecord goal, GoalsFacts f, ColonyModelInput input) => goal.Kind switch
    {
        GoalKind.ResearchProject or GoalKind.ResearchTier => Research(goal, f, input),
        GoalKind.WornCount => Worn(goal, f, input),
        _ => Unmodelled("no_tier2_path:" + GoalRules.KindSlug(goal.Kind)),
    };

    private static GoalTerminal Terminal(GoalRecord goal, GoalsFacts f, string[] nodes) =>
        new(GoalRules.NeedOf(goal).Value, GoalRules.ImportanceOf(goal), GoalRules.DeadlineOf(goal, f.Themes), EquatableArray.From(nodes));

    private static GoalPath Unmodelled(string reason) => new(new ProjectGraph(), null, reason);

    private static string Reason(string reason, ProjectGraph graph) => graph.Findings.Count == 0 ? reason : reason + ":" + graph.Findings[0];
}
