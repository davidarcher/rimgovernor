namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's project graph (#3530 of #3470, hunts #3620, per-stuff garments #3622): nodes in priority order, built from a ColonyModelInput. The builders
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

    /// <summary>The key of a pawn's skill stock (#3634): its marginal value is per XP under this key.</summary>
    public static string SkillKey(string pawn, string skill) => $"skill/{pawn}/{skill}";

    /// <summary>The key of a medicine kind's stock (#3634): its marginal value is per unit under this key.</summary>
    public static string MedicineKey(Resource kind) => $"medicine/{kind}";

    /// <summary>The id of the practice node of a slot.</summary>
    public static string PracticeId(string prisoner) => $"practice:{prisoner}";

    /// <summary>
    /// One practice node per practice slot (#3634), in slot order: the slot's surgery work, the skill its recipe trains, the raw XP per
    /// tick spent and the medicine a surgery uses up. A slot whose recipe learning is unqualified makes no node and is named. Practice
    /// never completes, so the review puts it last: it takes the hours the finite nodes leave.
    /// </summary>
    public static ProjectGraph Practice(ColonyModelInput input)
    {
        if (!input.Practice.TryGetValue(out var slots))
        {
            return Gap("practice_slots_unknown");
        }

        var nodes = new List<ProjectNode>();
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            if (!slot.RawXpPerWorkTick.TryGetValue(out var xp) || !(slot.Work > 0))
            {
                findings.Add($"practice_learning_unknown:{slot.Recipe}");
                continue;
            }

            nodes.Add(new ProjectNode
            {
                Id = PracticeId(slot.Prisoner), Kind = ProjectKind.Practice, Work = slot.Work, Skill = slot.Skill, RawXpPerWorkTick = xp, SpeedStat = slot.SpeedStat, Medicine = slot.Medicine,
            });
        }

        return new ProjectGraph { Nodes = EquatableArray.From(nodes), Findings = EquatableArray.From(findings) };
    }

    /// <summary>
    /// What a review runs: the research chain of the project being researched, and each field with a crop set, from sowing to
    /// harvest, gated on the research the crop's sowing waits on (<see cref="CropChoice.Research"/>). Then each herd of huntable
    /// wildlife is hunted and butchered (<see cref="Hunts"/>). Last, the garments the colony can make from the stuffs it holds or those
    /// nodes yield (<see cref="Garments"/>), so the harm model prices what wearing each avoids from the day it is made.
    /// </summary>
    public static ProjectGraph Standing(ColonyModelInput input, IReadOnlyList<GearOption> options, Fact<RaidThreat> threat)
    {
        var graph = input.Research.TryGetValue(out var research) && !research.Current.IsEmpty ? ResearchChain(input, research.Current) : new ProjectGraph();
        if (input.Land.TryGetValue(out var land))
        {
            foreach (var field in land.Where(f => f.Crop.Length > 0))
            {
                var gates = input.Crops.TryGetValue(out var crops) && crops.FirstOrDefault(c => c.Name == field.Crop) is { } choice ? choice.Research : default;
                graph = graph.Then(CropChain(input, field.Crop, field.Id, gates));
            }
        }

        graph = graph.Then(Hunts(input));
        return graph.Then(Garments(input, graph, options, threat));
    }

    /// <summary>The id of the garment node of a stuff in a gear slot.</summary>
    public static string GarmentId(Resource stuff, GearSlot slot) => $"garment:{stuff}:{slot}";

    /// <summary>
    /// One craft node per reachable stuff and gear slot (#3622): of the gear planner's bill options in that stuff and slot (not a
    /// slave's, a child's, a shield, a psychic or a smoke-pop garment), the def with the most damage avoided per hit against the
    /// colony's expected raid (<see cref="GearArmorTerm"/>), ties by option id; an option without armor makes no node. A stuff is
    /// reachable when the stock covers the garment's units of it, or a harvest or hunt node of <paramref name="sources"/> yields it.
    /// A node waits on the research its bill needs and takes its stuff from the run's ledger when it starts, so a sourced garment
    /// starts once its harvest or hunt has delivered.
    /// <para>
    /// Before the run a node is dropped when another node of its slot arrives no later and has at least its armor. Only a
    /// stock-covered garment is known to arrive no later: its stuff is in hand, its research is a subset of the other's and its craft
    /// work is no more. A sourced stuff's day is the run's, so two sourced garments both stay and the valuation keeps only the upgrade
    /// of the later one (<see cref="ColonyValue"/>).
    /// </para>
    /// The nodes are in armor order (most first), ties by id.
    /// </summary>
    public static ProjectGraph Garments(ColonyModelInput input, ProjectGraph sources, IReadOnlyList<GearOption> options, Fact<RaidThreat> threat)
    {
        var held = input.Stocks.Materials.TryGetValue(out var materials) ? materials.GroupBy(a => a.Resource).ToDictionary(g => g.Key, g => g.Sum(a => a.Count)) : [];
        var yielded = new HashSet<Resource>();
        foreach (var node in sources.Nodes)
        {
            if (node.Kind == ProjectKind.Harvest && input.Crops.TryGetValue(out var crops) && crops.FirstOrDefault(c => c.Name == node.Crop) is { } crop
                && crop.Harvests.TryGetValue(out var harvested))
            {
                yielded.Add(harvested);
            }
            else if (node.Kind == ProjectKind.Hunt)
            {
                yielded.UnionWith(node.Products.Select(p => p.Resource));
            }
        }

        var armor = new GearArmorTerm(threat);
        var gaps = new ProjectGraph();
        var garments = new List<Garment>();
        var picks = options.Where(o => o.Source == GearSource.Bill && !o.Stuff.IsEmpty && !o.SlaveOnly && !o.Shield && !o.Psychic && !o.Smokepop
                && !o.Definition.Value.StartsWith("Kid", StringComparison.Ordinal) && !o.Definition.Value.StartsWith("Apparel_Kid", StringComparison.Ordinal) && armor.Of(o) > 0)
            .GroupBy(o => (o.Stuff, o.Slot))
            .Select(g => g.OrderByDescending(armor.Of).ThenBy(o => o.ID, StringComparer.Ordinal).First());
        foreach (var garment in picks)
        {
            var sourced = yielded.Contains(garment.Stuff);
            if (!sourced && held.GetValueOrDefault(garment.Stuff) <= 0)
            {
                continue;
            }

            if (GoalPaths.RecipeFor(input, garment.Definition.Value, garment.Stuff) is not { } recipe)
            {
                gaps = gaps.Then(Gap($"garment_recipe_unknown:{garment.Definition.Value}"));
                continue;
            }

            var bill = garment.Research.Aggregate(new ProjectGraph(), (g, project) => g.Then(ResearchChain(input, new ResearchProjectId(project))));
            var after = garment.Research.Select(p => ResearchId(new ResearchProjectId(p))).Where(id => bill.Nodes.Any(n => n.Id == id));
            var craft = Craft(input, recipe.Recipe, EquatableArray.From(after), false, 1, garment.Stuff);
            if (craft.Nodes.Count == 0)
            {
                gaps = gaps.Then(craft);
                continue;
            }

            var node = craft.Nodes[0] with { Id = GarmentId(garment.Stuff, garment.Slot) };
            var covered = held.GetValueOrDefault(garment.Stuff) >= node.Ingredients.Where(a => a.Resource == garment.Stuff).Sum(a => a.Count);
            if (covered || sourced)
            {
                garments.Add(new(bill.Then(craft with { Nodes = EquatableArray.From(new[] { node }) }), node, garment.Slot, armor.Of(garment), covered));
            }
        }

        return garments.Where(b => !garments.Any(a => a.Dominates(b)))
            .OrderByDescending(g => g.Armor).ThenBy(g => g.Node.Id, StringComparer.Ordinal)
            .Aggregate(gaps, (graph, g) => graph.Then(g.Graph));
    }

    /// <summary>A garment node (<see cref="Garments"/>) with the research it waits on, and what its pruning compares.</summary>
    private sealed record Garment(ProjectGraph Graph, ProjectNode Node, GearSlot Slot, double Armor, bool Covered)
    {
        /// <summary>
        /// This garment arrives no later than <paramref name="other"/> in its slot and has at least its armor: it is stock-covered, its
        /// research is a subset of the other's and its work no more. Equal ones keep the stock-covered one, then the lower id.
        /// </summary>
        internal bool Dominates(Garment other) =>
            Node.Id != other.Node.Id && Slot == other.Slot && Covered && Armor >= other.Armor && Node.Work <= other.Node.Work
            && Node.Prerequisites.All(other.Node.Prerequisites.Contains)
            && (Armor > other.Armor || Node.Work < other.Node.Work || !other.Covered || Node.Prerequisites.Count < other.Node.Prerequisites.Count
                || string.CompareOrdinal(Node.Id, other.Node.Id) < 0);
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
