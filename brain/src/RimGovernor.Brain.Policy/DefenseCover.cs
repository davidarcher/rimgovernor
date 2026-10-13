namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // Native cover definitions the firing line may build.
    public const string DefenseSandbags = "Sandbags";
    public const string DefenseEmbrasure = "Embrasure";

    /// <summary>The Sandbags' stuff cost per placement.</summary>
    internal const int DefenseSandbagStuff = 5;

    /// <summary>
    /// Picks the firing line's cover. Real Sandbags replace the wooden Barricade stand-in when the definition
    /// is available and one fabric or leather stock covers every firing position; the stock the colony holds
    /// most of is used. An available Embrasure is named so the layout builds it on a firing line that runs along
    /// the perimeter wall. Anything unknown keeps base.
    /// </summary>
    public static DefenseDefinitions DefenseCoverChoice(ItemFacts items, DefenseDefinitions baseDefinitions, IReadOnlyDictionary<Resource, long> stock, bool stockKnown, bool sandbags, bool embrasure, int positions)
    {
        var output = baseDefinitions;
        if (embrasure)
        {
            output = output with { Embrasure = DefenseEmbrasure };
        }
        if (!sandbags || !stockKnown || positions <= 0)
        {
            return output;
        }
        var stuffs = items.StuffsFor(DefenseSandbags);
        var best = "";
        var most = 0L;
        foreach (var (resource, count) in stock)
        {
            var name = resource.Value;
            if (stuffs.Contains(resource) && (count > most || (count == most && string.CompareOrdinal(name, best) < 0)))
            {
                best = name;
                most = count;
            }
        }
        if (best != "" && most >= (long)positions * DefenseSandbagStuff)
        {
            output = output with { Sandbag = DefenseSandbags, SandbagStuff = best };
        }
        return output;
    }
}
