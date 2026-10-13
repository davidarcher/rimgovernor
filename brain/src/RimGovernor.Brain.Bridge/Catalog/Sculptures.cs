using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_sculptures.go.

public sealed partial class DefinitionCatalog
{
    /// <summary>The work one piece of a def made from a stuff takes (the stat's shown value with the stuff's factor); false when the catalog shows none.</summary>
    public (double Work, bool Shown) WorkToMake(string def, string stuff)
    {
        try
        {
            var (value, shown) = ShownStatValue(def, stuff, StatWorkToMake);
            return ((double)value, shown);
        }
        catch (Exception e) when (e is CatalogException or Domain.DomainException or StatException)
        {
            return (0, false);
        }
    }

    // The art recipes of the catalog: each recipe whose role is Sculpture, with the building it makes, the building's
    // footprint and stuff cost, and the work the recipe takes (its own workAmount, else the product's WorkToMake).
    // Smallest first: by stuff cost, then work, then name.
    private List<P.Sculpture> Sculptures()
    {
        var found = new List<P.Sculpture>();
        foreach (var name in Rows<RecipeDef>().Keys.Order(StringComparer.Ordinal))
        {
            if (RecipeRoleOf(name) != RecipeRole.Sculpture)
            {
                continue;
            }

            var recipe = Recipe(name);
            var product = recipe.products[0].thingDef.defName;
            var def = ThingRow(product);
            double work = recipe.workAmount;
            if (work <= 0)
            {
                work = def.statBases.Seq().Where(stat => stat.stat?.defName == StatWorkToMake).Select(stat => (double)stat.value).LastOrDefault();
            }

            if (work <= 0 || def.costStuffCount <= 0)
            {
                throw new CatalogException($"sculpture recipe {name} makes {product} with no work or no stuff cost");
            }

            found.Add(new P.Sculpture(name, product, new Domain.Cell(Math.Max(def.size.x, 1), Math.Max(def.size.z, 1)), def.costStuffCount, work));
        }

        return [.. found.OrderBy(s => s.Cost).ThenBy(s => s.Work).ThenBy(s => s.Recipe, StringComparer.Ordinal)];
    }
}
