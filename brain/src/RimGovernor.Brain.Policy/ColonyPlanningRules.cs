namespace RimGovernor.Brain.Policy;

public static partial class ColonyPlanningRules
{
    /// <summary>
    /// The first of the definition's stuff options whose cost list the colony stock covers, and how many of the
    /// definition that stock builds. None is null.
    /// </summary>
    public static (string Stuff, long Count)? StockedStuff(ReviewFacts facts, IReadOnlyDictionary<Resource, long> stock, string name)
    {
        foreach (var d in facts.Definitions)
        {
            if (d.Name != name)
            {
                continue;
            }
            foreach (var option in d.StuffOptions)
            {
                var count = -1L;
                foreach (var cost in option.Costs)
                {
                    if (cost.Count <= 0)
                    {
                        continue;
                    }
                    var n = stock.GetValueOrDefault(cost.Resource) / cost.Count;
                    if (count < 0 || n < count)
                    {
                        count = n;
                    }
                }
                if (count > 0)
                {
                    return (option.Stuff, count);
                }
            }
        }
        return null;
    }

    /// <summary>One planning definition's native availability, unknown when the census did not describe it.</summary>
    public static bool DefinitionAvailable(ReviewFacts facts, string name)
    {
        foreach (var d in facts.Definitions)
        {
            if (d.Name == name)
            {
                return d.Available;
            }
        }
        throw new RequiredFactException("planning definition:" + name);
    }

    /// <summary>Pairs the power family's generator definitions with their native availability, observed fuel stock and catalog delivery profile.</summary>
    public static IReadOnlyList<GeneratorOption> GeneratorOptions(ReviewFacts facts, IReadOnlyDictionary<Resource, long> stock, Func<Resource, bool> renewable)
    {
        var available = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var d in facts.Definitions)
        {
            available[d.Name] = d.Available;
        }
        return PowerRules.DefaultGeneratorOptions(
            name => available.TryGetValue(name, out var fact) ? fact : throw new RequiredFactException("generator definition:" + name),
            name => stock.GetValueOrDefault(name),
            renewable,
            facts.PowerSources,
            facts.GeneratorRows);
    }
}
