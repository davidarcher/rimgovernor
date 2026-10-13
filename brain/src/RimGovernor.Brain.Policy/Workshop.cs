namespace RimGovernor.Brain.Policy;

// Go: workshop.go and workshop_further.go.

/// <summary>What a MaintainResource deficit needs from the building ladder before a bill can be dispatched for it.</summary>
public enum WorkshopMethod
{
    /// <summary>A native fact the choice depends on is unobserved.</summary>
    [WireName("")]
    Unknown,

    /// <summary>A usable bench already hosts an available recipe for the resource; the bill path owns the deficit from here.</summary>
    [WireName("existing")]
    Existing,

    /// <summary>Stage Definition, a bench buildable now, in a Workshop-hosting room. NeedsPower marks a powered bench, which the power family connects once it stands as an unpowered consumer.</summary>
    [WireName("build")]
    Build,

    /// <summary>The first candidate bench and recipe are gated only by unfinished research; Research lists the projects to finish.</summary>
    [WireName("research")]
    Research,

    /// <summary>Every hosting bench needs more construction skill than any builder has, or power no generator definition can supply, which this ladder does not stage.</summary>
    [WireName("unavailable")]
    Unavailable,
}

/// <summary>The planning-census view of one bench definition.</summary>
public sealed record BenchDefinition
{
    public string Name { get; init; } = "";

    public Fact<bool> Available { get; init; }

    public Fact<bool> NeedsPower { get; init; }

    public Fact<int> ConstructionSkill { get; init; }

    /// <summary>Names the definition's own ResearchProjectDefs; with Available false they are what stands between the colony and the bench.</summary>
    public IReadOnlyList<string> Research { get; init; } = [];
}

public sealed record WorkshopRequest
{
    public Resource Resource { get; init; }

    public Fact<EquatableArray<GearBench>> Benches { get; init; }

    public IReadOnlyList<RecipeHost> Hosts { get; init; } = [];

    public IReadOnlyList<BenchDefinition> Definitions { get; init; } = [];

    /// <summary>Whether a generator definition is available to build, so a powered bench can be staged and connected; unknown or false keeps the choice to unpowered benches.</summary>
    public Fact<bool> Power { get; init; }

    /// <summary>
    /// The best Construction level among colonists who can build (BuilderSkill); a bench with a construction skill
    /// prerequisite is buildable only when a builder meets it, and the plan's own work requirement then assigns that
    /// builder. Unknown keeps the choice to benches without a prerequisite.
    /// </summary>
    public Fact<int> BuilderSkill { get; init; }

    /// <summary>Names the bench kinds (FurtherBenchKinds) whose standing benches no longer suffice: a bench of such a kind is not reused, and a further one of the kind is staged instead.</summary>
    public IReadOnlyList<string> Further { get; init; } = [];
}

public sealed record WorkshopChoice
{
    public WorkshopMethod Method { get; init; }

    public string Definition { get; init; } = "";

    public string Recipe { get; init; } = "";

    public bool NeedsPower { get; init; }

    /// <summary>The sorted project set WorkshopResearch waits on.</summary>
    public IReadOnlyList<string> Research { get; init; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>How many colonists justify each bench of one kind beyond the first: the cap on a kind is 1 + colonists/this.</summary>
    public const int ColonistsPerExtraBench = 4;

    /// <summary>Bounds a kind however large the colony grows.</summary>
    public const int MaxBenchesPerKind = 4;

    /// <summary>The most benches of one kind the ladder stands for a colony of the given size.</summary>
    public static int BenchCap(int colonists) => Math.Min(MaxBenchesPerKind, 1 + (Math.Max(colonists, 0) / ColonistsPerExtraBench));

    /// <summary>
    /// The bench kinds that should get another bench: the dispatcher reports a shortfall only a new bench can close
    /// (no_bench, benches_exhausted) and the kind is below BenchCap. Other reasons (slots, ingredients, hauling) are
    /// not helped by a bench and never trigger.
    /// </summary>
    public static IReadOnlyList<string> FurtherBenchKinds(IReadOnlyList<UnmetThroughput> unmet, IReadOnlyList<GearBench> benches, int colonists)
    {
        var standing = new Dictionary<string, int>();
        foreach (var b in benches)
        {
            standing[b.Def] = standing.GetValueOrDefault(b.Def) + 1;
        }
        var output = new List<string>();
        foreach (var u in unmet)
        {
            if (u.ShortPerDay <= 0 || (u.Reason != UnmetReason.NoBench && u.Reason != UnmetReason.BenchesExhausted))
            {
                continue;
            }
            if (standing.GetValueOrDefault(u.BenchKind) < BenchCap(colonists))
            {
                output.Add(u.BenchKind);
            }
        }
        output.Sort(StringComparer.Ordinal);
        return output;
    }

    /// <summary>The highest Construction skill level among the pawns that are available, apply to work assignment and have the skill enabled; unknown when the roster or any such pawn's skills are unobserved.</summary>
    public static Fact<int> BuilderSkill(Fact<EquatableArray<WorkPawn>> pawns)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<int>();
        }
        var best = 0;
        foreach (var pawn in rows)
        {
            var ak = pawn.Available.TryGetValue(out var available);
            var pk = pawn.Applies.TryGetValue(out var applies);
            if (!ak || !pk)
            {
                return Unknown<int>();
            }
            if (!available || !applies)
            {
                continue;
            }
            if (!pawn.Skills.TryGetValue(out var skills))
            {
                return Unknown<int>();
            }
            foreach (var skill in skills)
            {
                if (skill.Name == "Construction" && !skill.Disabled)
                {
                    best = Math.Max(best, skill.Level);
                }
            }
        }
        return Known(best);
    }

    /// <summary>Lists, in stable order, every bench definition hosting a research-available recipe that produces the resource. It is the definition set a planner requests from the planning census before choosing.</summary>
    public static IReadOnlyList<string> WorkshopBenchCandidates(Resource resource, IReadOnlyList<RecipeHost> hosts) => WorkshopCandidates(resource, hosts, false);

    /// <summary>Lists every bench definition hosting any recipe for the resource, research-gated ones included, so the census can report which projects each is waiting on.</summary>
    public static IReadOnlyList<string> WorkshopResearchCandidates(Resource resource, IReadOnlyList<RecipeHost> hosts) => WorkshopCandidates(resource, hosts, true);

    private static List<string> WorkshopCandidates(Resource resource, IReadOnlyList<RecipeHost> hosts, bool gated)
    {
        var seen = new HashSet<string>();
        var output = new List<string>();
        foreach (var host in hosts)
        {
            if ((!gated && !host.Available) || !ContainsResource(host.Products, resource))
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
    /// Decides whether a resource deficit already has a bench (reuse first), needs one staged (the first candidate
    /// the census reports buildable by a builder the colony has, unpowered before powered), is waiting on research
    /// (the first candidate gated by nothing else), or cannot be served yet. It issues no orders and reserves
    /// nothing. Throws on an invalid resource.
    /// </summary>
    public static WorkshopChoice SelectWorkshopBench(WorkshopRequest r)
    {
        if (!ValidResource(r.Resource))
        {
            throw new DomainException("invalid workshop resource");
        }
        if (!r.Benches.TryGetValue(out var benches))
        {
            return new WorkshopChoice { Method = WorkshopMethod.Unknown };
        }
        WorkshopChoice? reuse = null;
        foreach (var b in benches)
        {
            if (!b.Recipes.TryGetValue(out var recipes))
            {
                return new WorkshopChoice { Method = WorkshopMethod.Unknown };
            }
            foreach (var recipe in recipes)
            {
                if (!ContainsResource(recipe.Products, r.Resource))
                {
                    continue;
                }
                var ak = recipe.Available.TryGetValue(out var available);
                var ok = recipe.AvailableOn.TryGetValue(out var on);
                if (!ak || !ok)
                {
                    return new WorkshopChoice { Method = WorkshopMethod.Unknown };
                }
                if (available && on)
                {
                    var existing = new WorkshopChoice { Method = WorkshopMethod.Existing, Definition = "", Recipe = recipe.Definition };
                    if (!r.Further.Contains(b.Def))
                    {
                        return existing;
                    }

                    // Capacity no longer suffices: a further bench of the kind, with this one as the fallback when none can be staged.
                    reuse = existing;
                }
            }
        }
        var byName = new Dictionary<string, BenchDefinition>();
        foreach (var d in r.Definitions)
        {
            byName[d.Name] = d;
        }
        var powerKnown = r.Power.TryGetValue(out var power);
        var builderKnown = r.BuilderSkill.TryGetValue(out var builder);
        bool Buildable(int skill) => skill == 0 || (builderKnown && skill <= builder);
        var unknown = false;

        // Buildable now: unpowered first, then powered when a generator can be built for it.
        foreach (var allowPower in new[] { false, true })
        {
            if (allowPower && !(powerKnown && power))
            {
                continue;
            }
            foreach (var name in WorkshopBenchCandidates(r.Resource, r.Hosts))
            {
                if (reuse is not null && !r.Further.Contains(name))
                {
                    continue;
                }
                var exists = byName.TryGetValue(name, out var d);
                if (!exists || !d!.Available.TryGetValue(out var available) || !d.NeedsPower.TryGetValue(out var powered) || !d.ConstructionSkill.TryGetValue(out var skill))
                {
                    unknown = true;
                    continue;
                }
                if (available && powered == allowPower && Buildable(skill))
                {
                    return new WorkshopChoice { Method = WorkshopMethod.Build, Definition = name, Recipe = WorkshopRecipe(r.Resource, r.Hosts, name), NeedsPower = powered };
                }
            }
        }
        if (reuse is not null)
        {
            return reuse;
        }

        // Research-gated: the first bench and recipe pair that only research stands between; its projects are the ladder's next rung.
        foreach (var host in r.Hosts)
        {
            if (!ContainsResource(host.Products, r.Resource))
            {
                continue;
            }
            foreach (var name in host.Benches)
            {
                var exists = byName.TryGetValue(name, out var d);
                if (!exists || !d!.Available.TryGetValue(out var available) || !d.NeedsPower.TryGetValue(out var powered) || !d.ConstructionSkill.TryGetValue(out var skill))
                {
                    unknown = true;
                    continue;
                }
                if (!Buildable(skill) || (powered && !(powerKnown && power)))
                {
                    continue;
                }

                // A gate the census cannot name (a recipe or bench unavailable with no project listed) is not research this ladder can do.
                var projects = new List<string>();
                if (!host.Available)
                {
                    if (host.Research.Count == 0)
                    {
                        continue;
                    }
                    projects.AddRange(host.Research);
                }
                if (!available)
                {
                    if (d.Research.Count == 0)
                    {
                        continue;
                    }
                    projects.AddRange(d.Research);
                }
                if (projects.Count == 0)
                {
                    continue;
                }
                projects.Sort(StringComparer.Ordinal);
                return new WorkshopChoice
                {
                    Method = WorkshopMethod.Research,
                    Definition = name,
                    Recipe = host.Definition,
                    NeedsPower = powered,
                    Research = [.. projects.Distinct()],
                };
            }
        }
        return new WorkshopChoice { Method = unknown ? WorkshopMethod.Unknown : WorkshopMethod.Unavailable };
    }

    private static string WorkshopRecipe(Resource resource, IReadOnlyList<RecipeHost> hosts, string bench)
    {
        foreach (var host in hosts)
        {
            if (!host.Available || !ContainsResource(host.Products, resource))
            {
                continue;
            }
            foreach (var b in host.Benches)
            {
                if (b == bench)
                {
                    return host.Definition;
                }
            }
        }
        return "";
    }
}
