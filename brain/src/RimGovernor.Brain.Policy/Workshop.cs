namespace RimGovernor.Brain.Policy;

/// <summary>The planning-census view of one bench definition.</summary>
public sealed record BenchDefinition
{
    public string Name { get; init; } = "";

    public bool Available { get; init; }

    /// <summary>Names the definition's own ResearchProjectDefs; with Available false they are what stands between the colony and the bench.</summary>
    public IReadOnlyList<string> Research { get; init; } = [];
}

/// <summary>
/// The research rung of a resource no bench can host yet (RG-HABITAT-005): the first host definition and recipe only
/// research stands between, and the sorted projects to finish. Which bench, how many and where are never chosen here:
/// a buildable host is the shared layout comparison's (LayoutHostRules).
/// </summary>
public sealed record WorkshopRung(string Bench, string Recipe, IReadOnlyList<string> Research);

public static partial class WorkRules
{
    /// <summary>Lists, in stable order, every bench definition hosting any recipe for the resource, research-gated ones included, so the census can report which projects each is waiting on.</summary>
    public static IReadOnlyList<string> WorkshopResearchCandidates(Resource resource, IReadOnlyList<RecipeHost> hosts)
    {
        var seen = new HashSet<string>();
        var output = new List<string>();
        foreach (var host in hosts)
        {
            if (!GearRules.ContainsResource(host.Products, resource))
            {
                continue;
            }
            foreach (var bench in host.Benches)
            {
                if (seen.Add(bench))
                {
                    output.Add(bench);
                }
            }
        }
        output.Sort(StringComparer.Ordinal);
        return output;
    }

    /// <summary>
    /// The available recipe for <paramref name="resource"/> a usable standing bench already offers, or null. Such a bench
    /// lists the recipe, so the declarer names its kind (GearOrdersRules.GearBenchDef) and its bills run there; a load no
    /// standing bench lists is unhosted and its host is the layout comparison's. Throws on an invalid resource.
    /// </summary>
    public static string? StandingWorkshopRecipe(Resource resource, IReadOnlyList<GearBench> benches)
    {
        if (!AdmissionRules.ValidResource(resource)) throw new DomainException("invalid workshop resource");
        foreach (var bench in benches)
            foreach (var recipe in bench.Recipes)
                if (GearRules.ContainsResource(recipe.Products, resource) && recipe.Available && recipe.AvailableOn) return recipe.Definition;
        return null;
    }

    /// <summary>Whether an available recipe making <paramref name="resource"/> has an available bench definition: the shared layout comparison can host it.</summary>
    public static bool WorkshopHostable(Resource resource, IReadOnlyList<RecipeHost> hosts, IReadOnlyList<BenchDefinition> definitions)
    {
        var byName = new Dictionary<string, BenchDefinition>(StringComparer.Ordinal);
        foreach (var d in definitions) byName[d.Name] = d;
        return hosts.Where(h => GearRules.ContainsResource(h.Products, resource)).Any(h => h.Available && h.Benches.Any(b => byName.TryGetValue(b, out var d) ? d.Available : throw new RequiredFactException("bench definition:" + b)));
    }

    /// <summary>
    /// The research rung for <paramref name="resource"/>: null when an available definition already hosts an available recipe
    /// for it (the comparison prices that host, its power and its room), or no gate names a project; else the first host
    /// definition and recipe pair whose only gates are research the census names, with its sorted projects. A host
    /// definition the census does not describe throws <see cref="RequiredFactException"/>.
    /// </summary>
    public static WorkshopRung? WorkshopResearchRung(Resource resource, IReadOnlyList<RecipeHost> hosts, IReadOnlyList<BenchDefinition> definitions)
    {
        var byName = new Dictionary<string, BenchDefinition>(StringComparer.Ordinal);
        foreach (var d in definitions) byName[d.Name] = d;
        BenchDefinition Definition(string name) => byName.TryGetValue(name, out var d) ? d : throw new RequiredFactException("bench definition:" + name);
        var producing = hosts.Where(h => GearRules.ContainsResource(h.Products, resource)).ToArray();
        if (producing.Any(h => h.Available && h.Benches.Any(b => Definition(b).Available))) return null;
        foreach (var host in producing)
        {
            foreach (var name in host.Benches)
            {
                var d = Definition(name);
                // A gate the census cannot name (a recipe or bench unavailable with no project listed) is not research this rung can do.
                if (!host.Available && host.Research.Count == 0 || !d.Available && d.Research.Count == 0) continue;
                var projects = (host.Available ? [] : host.Research).Concat(d.Available ? [] : d.Research).Distinct().Order(StringComparer.Ordinal).ToArray();
                if (projects.Length != 0) return new WorkshopRung(name, host.Definition, projects);
            }
        }
        return null;
    }
}
