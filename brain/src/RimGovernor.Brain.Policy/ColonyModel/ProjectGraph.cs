namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's project graph (#3530 of #3470, hunts #3620): nodes in priority order, built from a ColonyModelInput. The builders
// read only facts the input carries; a fact the input lacks leaves the nodes that need it out and is named in Findings, never
// defaulted. The labor allocation and the growth integration are in ProjectRun and CropGrowthModel.

/// <summary>The nodes in priority order and what building them could not read.</summary>
public sealed record ProjectGraph
{
    public EquatableArray<ProjectNode> Nodes { get; init; }

    /// <summary>A gap in the facts the nodes were built from, one finding each.</summary>
    public EquatableArray<string> Findings { get; init; }

    /// <summary>This graph followed by <paramref name="next"/>; the nodes keep their order and a node already here is not added twice (two goals share a research prerequisite), the findings are sorted and distinct.</summary>
    public ProjectGraph Then(ProjectGraph next) => new()
    {
        Nodes = EquatableArray.From(Nodes.Concat(next.Nodes.Where(n => Nodes.All(have => have.Id != n.Id)))),
        Findings = EquatableArray.From(Findings.Concat(next.Findings).Distinct().Order(StringComparer.Ordinal)),
    };

    private const string ResearchPrefix = "research:";

    /// <summary>Research points per work tick at research speed 1 (<c>ResearchManager.ResearchPerformed</c>; kept constants).</summary>
    public const double ResearchPointsPerWorkTick = 0.00825;

    /// <summary>The id of the research node of a project.</summary>
    public static string ResearchId(ResearchProjectId project) => $"{ResearchPrefix}{project}";

    /// <summary>The id of the sowing node of a field (the field is the native zone id the sowing read names).</summary>
    public static string SowId(string field) => ForecastProjectProgress.SowPrefix + field;

    /// <summary>
    /// The key tier-1 progress is credited under (<see cref="ForecastProjectProgress.Project"/>): a research node's project name,
    /// and a sowing node's id. The unit is the node's: research points for research, cells for sowing.
    /// </summary>
    public static string ProgressKey(ProjectNode node) => node.Kind == ProjectKind.Research ? node.Id[ResearchPrefix.Length..] : node.Id;

    /// <summary>
    /// What a review runs: the research chain of the project being researched, and each field with a crop set, from sowing to
    /// harvest, gated on the research the crop's sowing waits on (<see cref="CropChoice.Research"/>). A crop whose harvest is the
    /// stuff of a garment the colony could craft (<see cref="Garment"/>) also gets that garment's craft after its harvests, so the
    /// harm model prices what wearing it avoids. Last, each herd of huntable wildlife is hunted and butchered (<see cref="Hunts"/>).
    /// </summary>
    public static ProjectGraph Standing(ColonyModelInput input, IReadOnlyList<GearOption> options, Fact<RaidThreat> threat)
    {
        var graph = input.Research.TryGetValue(out var research) && !research.Current.IsEmpty ? ResearchChain(input, research.Current) : new ProjectGraph();
        if (input.Land.TryGetValue(out var land))
        {
            var sown = land.Where(f => f.Crop.Length > 0).ToList();
            foreach (var field in sown)
            {
                var gates = input.Crops.TryGetValue(out var crops) && crops.FirstOrDefault(c => c.Name == field.Crop) is { } choice ? choice.Research : default;
                graph = graph.Then(CropChain(input, field.Crop, field.Id, gates));
            }

            foreach (var crop in sown.Select(f => f.Crop).Distinct())
            {
                var harvests = sown.Where(f => f.Crop == crop && graph.Nodes.Any(n => n.Id == $"harvest:{f.Id}")).Select(f => $"harvest:{f.Id}").ToArray();
                if (harvests.Length > 0)
                {
                    graph = graph.Then(GarmentCraft(input, crop, harvests, options, threat));
                }
            }
        }

        return graph.Then(Hunts(input));
    }

    /// <summary>The id of the hunt node of a race.</summary>
    public static string HuntId(Resource race) => $"hunt:{race}";

    /// <summary>
    /// One hunt-and-butcher node per herd of the wildlife census (#3620), in race order: the hunt planner's pursuit work, staffed
    /// in daylight by the pawns who may hunt; at completion the meat joins nutrition and the butchery products (leather, rounded
    /// to whole units) join materials, where a craft can take them. Butchering takes no labor here: its work is not read.
    /// </summary>
    public static ProjectGraph Hunts(ColonyModelInput input)
    {
        if (!input.Wildlife.TryGetValue(out var herds))
        {
            return Gap("wildlife_unknown");
        }

        var nodes = herds.Select(h => new ProjectNode
        {
            Id = HuntId(h.Race),
            Kind = ProjectKind.Hunt,
            Work = h.Work,
            Nutrition = h.Nutrition,
            Products = EquatableArray.From(h.Products.Select(p => new Amount(p.Def, (long)Math.Round(p.Amount, MidpointRounding.AwayFromZero))).Where(a => a.Count > 0)),
        }).ToArray();
        return new ProjectGraph
        {
            Nodes = EquatableArray.From(nodes),
            Findings = nodes.Length == 0 ? default : Gap("butcher_work_unread").Findings,
        };
    }

    /// <summary>
    /// The one garment a crop's harvest is made into: of the bill options made from the crop's yield (not a slave's, a child's, a
    /// shield, a psychic or a smoke-pop garment), the one the gear planner scores highest, its damage avoided per hit against the
    /// colony's expected raid (<see cref="GearArmorTerm"/>), ties by option id. Null when the crop yields no stuff a garment is made
    /// of or none of them has armor.
    /// </summary>
    public static GearOption? Garment(CropChoice crop, IReadOnlyList<GearOption> options, Fact<RaidThreat> threat)
    {
        if (!crop.Harvests.TryGetValue(out var stuff))
        {
            return null;
        }

        var armor = new GearArmorTerm(threat);
        return options.Where(o => o.Source == GearSource.Bill && o.Stuff == stuff && !o.SlaveOnly && !o.Shield && !o.Psychic && !o.Smokepop
                && !o.Definition.Value.StartsWith("Kid", StringComparison.Ordinal) && !o.Definition.Value.StartsWith("Apparel_Kid", StringComparison.Ordinal) && armor.Of(o) > 0)
            .OrderByDescending(armor.Of).ThenBy(o => o.ID, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>The craft of the crop's garment (<see cref="Garment"/>) after its harvests and the research the garment's bill waits on; empty when the crop has none.</summary>
    private static ProjectGraph GarmentCraft(ColonyModelInput input, string crop, string[] harvests, IReadOnlyList<GearOption> options, Fact<RaidThreat> threat)
    {
        if (!input.Crops.TryGetValue(out var crops) || crops.FirstOrDefault(c => c.Name == crop) is not { } choice || Garment(choice, options, threat) is not { } garment)
        {
            return new ProjectGraph();
        }

        var recipe = GoalPaths.RecipeFor(input, garment.Definition.Value, garment.Stuff);
        if (recipe is null)
        {
            return Gap($"garment_recipe_unknown:{garment.Definition.Value}");
        }

        var bill = garment.Research.Aggregate(new ProjectGraph(), (graph, project) => graph.Then(ResearchChain(input, new ResearchProjectId(project))));
        var after = harvests.Concat(garment.Research.Select(p => ResearchId(new ResearchProjectId(p))).Where(id => bill.Nodes.Any(n => n.Id == id)));
        return bill.Then(Craft(input, recipe.Recipe, EquatableArray.From(after), false, 1, garment.Stuff));
    }

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
    /// A crop from research to harvest on one field: the research the sowing needs (<paramref name="gates"/>, none when empty), then
    /// sowing, growth and one harvest. The cells sown are the field's cells at or above the crop's fertility minimum.
    /// </summary>
    public static ProjectGraph CropChain(ColonyModelInput input, string crop, string field, EquatableArray<ResearchProjectId> gates = default)
    {
        var research = gates.Aggregate(new ProjectGraph(), (graph, gate) => graph.Then(ResearchChain(input, gate)));
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

        var sow = SowId(field);
        var grow = $"grow:{field}";
        var after = gates.Select(ResearchId).Where(id => research.Nodes.Any(n => n.Id == id)).ToArray();
        return research.Then(new ProjectGraph
        {
            Nodes = EquatableArray.From(new[]
            {
                new ProjectNode { Id = sow, Kind = ProjectKind.Sow, Prerequisites = EquatableArray.From(after), Work = cells * sowWork, Field = field, Crop = crop, Cells = cells },
                new ProjectNode { Id = grow, Kind = ProjectKind.Grow, Prerequisites = EquatableArray.From(new[] { sow }), Field = field, Crop = crop, Cells = cells },
                new ProjectNode { Id = $"harvest:{field}", Kind = ProjectKind.Harvest, Prerequisites = EquatableArray.From(new[] { grow }), Work = cells * harvestWork, Field = field, Crop = crop, Cells = cells },
            }),
        });
    }

    /// <summary>
    /// One run of a recipe after <paramref name="after"/>. The ingredients are the first alternative of each slot (named when a slot has
    /// several); the products go to the materials stock when <paramref name="productsToStock"/>, otherwise they are apparel.
    /// <paramref name="copies"/> runs of the recipe are one node: its work, ingredients and products are that many times the recipe's.
    /// </summary>
    public static ProjectGraph Craft(ColonyModelInput input, string recipe, EquatableArray<string> after, bool productsToStock, int copies = 1, Resource? stuff = null)
    {
        if (!input.Recipes.TryGetValue(out var recipes) || recipes.FirstOrDefault(r => r.Recipe == recipe) is not { } row)
        {
            return Gap($"recipe_unknown:{recipe}");
        }

        if (!row.WorkAmount.TryGetValue(out var work) || !row.Slots.TryGetValue(out var slots) || !row.Products.TryGetValue(out var products))
        {
            return Gap($"recipe_unread:{recipe}");
        }

        // The stuff the craft is made from picks its alternative; a slot with several alternatives and none of that stuff takes the first.
        Amount Pick(EquatableArray<Amount> slot) => stuff is { } named && slot.FirstOrDefault(a => a.Resource == named && a.Count > 0) is { } match && match.Count > 0 ? match : slot[0];
        var findings = slots.Any(s => s.Count > 1 && !(stuff is { } named && s.Any(a => a.Resource == named && a.Count > 0))) ? EquatableArray.From(new[] { $"craft_alternative_first:{recipe}" }) : default;
        var ingredients = slots.Where(s => s.Count > 0).Select(s => Pick(s) with { Count = Pick(s).Count * copies });
        return new ProjectGraph
        {
            Nodes = EquatableArray.From(new[]
            {
                new ProjectNode { Id = $"craft:{recipe}", Kind = ProjectKind.Craft, Prerequisites = after, Work = work * copies, Ingredients = EquatableArray.From(ingredients), Products = EquatableArray.From(products.Select(p => p with { Count = p.Count * copies })), ProductsToStock = productsToStock },
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
