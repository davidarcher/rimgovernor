namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's project graph (#3530 of #3470): nodes in priority order, built from a ColonyModelInput. The builders
// read only facts the input carries; a fact the input lacks leaves the nodes that need it out and is named in Findings, never
// defaulted. The labor allocation and the growth integration are in ProjectRun and CropGrowthModel.

/// <summary>The nodes in priority order and what building them could not read.</summary>
public sealed record ProjectGraph
{
    public EquatableArray<ProjectNode> Nodes { get; init; }

    /// <summary>A gap in the facts the nodes were built from, one finding each.</summary>
    public EquatableArray<string> Findings { get; init; }

    /// <summary>This graph followed by <paramref name="next"/>; the nodes keep their order, the findings are sorted and distinct.</summary>
    public ProjectGraph Then(ProjectGraph next) => new()
    {
        Nodes = EquatableArray.From(Nodes.Concat(next.Nodes)),
        Findings = EquatableArray.From(Findings.Concat(next.Findings).Distinct().Order(StringComparer.Ordinal)),
    };

    /// <summary>The id of the research node of a project.</summary>
    public static string ResearchId(ResearchProjectId project) => $"research:{project}";

    /// <summary>
    /// The unfinished research the project needs: its unfinished prerequisites first, then the project, each a node with the
    /// points still to spend (cost less the researched fraction). An unread progress counts the whole cost and is named.
    /// </summary>
    public static ProjectGraph ResearchChain(ColonyModelInput input, ResearchProjectId project)
    {
        if (!input.Research.TryGetValue(out var research))
        {
            return Gap("research_unknown");
        }

        var nodes = new List<ProjectNode>();
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var done = research.Finished.ToHashSet();
        var seen = new HashSet<ResearchProjectId>();

        void Visit(ResearchProjectId id)
        {
            if (done.Contains(id) || !seen.Add(id))
            {
                return;
            }

            if (research.Projects.FirstOrDefault(p => p.Project == id) is not { } listed)
            {
                findings.Add($"research_project_unknown:{id}");
                return;
            }

            foreach (var prerequisite in listed.Prerequisites)
            {
                Visit(prerequisite);
            }

            var progress = 0.0;
            if (!listed.Progress.TryGetValue(out progress))
            {
                findings.Add($"research_progress_unknown:{id}");
            }

            nodes.Add(new ProjectNode
            {
                Id = ResearchId(id),
                Kind = ProjectKind.Research,
                Prerequisites = EquatableArray.From(listed.Prerequisites.Where(p => !done.Contains(p)).Select(ResearchId)),
                Work = listed.Cost * (1 - progress),
            });
        }

        Visit(project);
        return new ProjectGraph { Nodes = EquatableArray.From(nodes), Findings = EquatableArray.From(findings) };
    }

    /// <summary>
    /// A crop from research to harvest on one field: the research the sowing needs (<paramref name="gate"/>, none when empty), then
    /// sowing, growth and one harvest. The cells sown are the field's cells at or above the crop's fertility minimum.
    /// </summary>
    public static ProjectGraph CropChain(ColonyModelInput input, string crop, string field, ResearchProjectId gate = default)
    {
        var research = gate.IsEmpty ? new ProjectGraph() : ResearchChain(input, gate);
        if (!input.Crops.TryGetValue(out var crops) || crops.FirstOrDefault(c => c.Name == crop) is not { } choice)
        {
            return research.Then(Gap($"crop_unknown:{crop}"));
        }

        if (!input.Land.TryGetValue(out var land) || land.FirstOrDefault(f => f.Id == field) is not { } zone)
        {
            return research.Then(Gap($"field_unknown:{field}"));
        }

        if (!choice.FertilityMin.TryGetValue(out var minimum) || !choice.SowWork.TryGetValue(out var sowWork) || !choice.HarvestWork.TryGetValue(out var harvestWork))
        {
            return research.Then(Gap($"crop_work_unknown:{crop}"));
        }

        var cells = zone.Fertility.Count(f => f >= minimum);
        if (cells == 0)
        {
            return research.Then(Gap($"no_fertile_cells:{field}"));
        }

        var sow = $"sow:{field}";
        var grow = $"grow:{field}";
        string[] gates = !gate.IsEmpty && research.Nodes.Any(n => n.Id == ResearchId(gate)) ? [ResearchId(gate)] : [];
        return research.Then(new ProjectGraph
        {
            Nodes = EquatableArray.From(new[]
            {
                new ProjectNode { Id = sow, Kind = ProjectKind.Sow, Prerequisites = EquatableArray.From(gates), Work = cells * sowWork, Field = field, Crop = crop, Cells = cells },
                new ProjectNode { Id = grow, Kind = ProjectKind.Grow, Prerequisites = EquatableArray.From(new[] { sow }), Field = field, Crop = crop, Cells = cells },
                new ProjectNode { Id = $"harvest:{field}", Kind = ProjectKind.Harvest, Prerequisites = EquatableArray.From(new[] { grow }), Work = cells * harvestWork, Field = field, Crop = crop, Cells = cells },
            }),
        });
    }

    /// <summary>
    /// One run of a recipe after <paramref name="after"/>. The ingredients are the first alternative of each slot (named when a slot has
    /// several); the products go to the materials stock when <paramref name="productsToStock"/>, otherwise they are apparel.
    /// </summary>
    public static ProjectGraph Craft(ColonyModelInput input, string recipe, EquatableArray<string> after, bool productsToStock)
    {
        if (!input.Recipes.TryGetValue(out var recipes) || recipes.FirstOrDefault(r => r.Recipe == recipe) is not { } row)
        {
            return Gap($"recipe_unknown:{recipe}");
        }

        if (!row.WorkAmount.TryGetValue(out var work) || !row.Slots.TryGetValue(out var slots) || !row.Products.TryGetValue(out var products))
        {
            return Gap($"recipe_unread:{recipe}");
        }

        var findings = slots.Any(s => s.Count > 1) ? EquatableArray.From(new[] { $"craft_alternative_first:{recipe}" }) : default;
        var ingredients = slots.Where(s => s.Count > 0).Select(s => s[0]);
        return new ProjectGraph
        {
            Nodes = EquatableArray.From(new[]
            {
                new ProjectNode { Id = $"craft:{recipe}", Kind = ProjectKind.Craft, Prerequisites = after, Work = work, Ingredients = EquatableArray.From(ingredients), Products = products, ProductsToStock = productsToStock },
            }),
            Findings = findings,
        };
    }

    /// <summary>The work left on a standing blueprint or frame, with the materials it still owes, after <paramref name="after"/>.</summary>
    public static ProjectGraph Construction(ColonyModelInput input, string site, EquatableArray<string> after)
    {
        if (!input.Sites.TryGetValue(out var sites) || sites.FirstOrDefault(s => s.Id == site) is not { } row)
        {
            return Gap($"site_unknown:{site}");
        }

        if (!row.WorkLeft.TryGetValue(out var work) || !row.MaterialsOwed.TryGetValue(out var owed))
        {
            return Gap($"site_unread:{site}");
        }

        return new ProjectGraph
        {
            Nodes = EquatableArray.From(new[] { new ProjectNode { Id = $"construction:{site}", Kind = ProjectKind.Construction, Prerequisites = after, Work = work, Ingredients = owed } }),
        };
    }

    private static ProjectGraph Gap(string finding) => new() { Findings = EquatableArray.From(new[] { finding }) };
}
