namespace RimGovernor.Brain.Policy;

/// <summary>
/// Which stuffs a gear bill option may be made from: the colony holds the units the option takes, or a known recipe
/// chain makes the stuff. A stuff is made when a recipe whose research is finished produces it and every ingredient
/// slot of that recipe has a member in stock or itself made (a cycle in the chain makes nothing). Stats pick among the
/// options this admits, so a better stuff with no supply route is never chosen (#2467). One instance serves one census;
/// it remembers the stuffs it has found made.
/// </summary>
public sealed class GearObtainability(IReadOnlyDictionary<Resource, long> stock, Func<Resource, IReadOnlyList<RecipeHost>> recipes)
{
    private readonly Dictionary<Resource, bool> _made = [];

    /// <summary>
    /// Whether the option can be had: a worn, loose or stored item, a bill for a def made from no stuff, or a bill whose
    /// stuff the stock covers (the units its ingredients take) or a known recipe chain makes.
    /// </summary>
    public bool Obtainable(GearOption o)
    {
        if (o.Source != GearSource.Bill || o.Stuff.IsEmpty)
        {
            return true;
        }
        var units = o.Ingredients.Where(a => a.Resource == o.Stuff).Sum(a => a.Count);
        return stock.GetValueOrDefault(o.Stuff) >= Math.Max(1, units) || Made(o.Stuff);
    }

    private bool Made(Resource stuff)
    {
        if (!_made.TryGetValue(stuff, out var made))
        {
            _made[stuff] = made = Made(stuff, []);
        }
        return made;
    }

    // A stuff found made stays made whatever chain asked; one found unmade is remembered only from the top (Made(stuff)),
    // since a deeper miss may be the cycle guard's.
    private bool Made(Resource r, HashSet<Resource> chain)
    {
        if (_made.GetValueOrDefault(r))
        {
            return true;
        }
        if (!chain.Add(r))
        {
            return false;
        }
        var made = false;
        foreach (var host in recipes(r))
        {
            if (host.Available && host.Ingredients.TryGetValue(out var slots)
                && slots.All(slot => slot.Any(a => stock.GetValueOrDefault(a.Resource) > 0 || Made(a.Resource, chain))))
            {
                made = true;
                break;
            }
        }
        chain.Remove(r);
        if (made)
        {
            _made[r] = true;
        }
        return made;
    }
}
