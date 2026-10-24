using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The recipes the stock-arrival model crafts from (#3868): what makes a medicine, or a part an install recipe puts back.

public sealed partial class DefinitionCatalog
{
    private sealed record StockRecipeRow(P.StockRecipe Recipe, IReadOnlyList<string> Research);

    private IReadOnlyList<StockRecipeRow>? _stockRecipes;

    /// <summary>
    /// The recipes a stock a rule waits on can be crafted from (#3868, <see cref="P.StockRecipe"/>), available with
    /// <paramref name="finished"/> research: each recipe whose product is a medicine (<c>ThingDef.IsMedicine</c>) or the fixed part
    /// ingredient of a Recipe_InstallNaturalBodyPart or Recipe_InstallArtificialBodyPart recipe, at the player-buildable benches a
    /// DoBill giver of a modelled work type serves it at, with its skill requirements, workSpeedStat and workTableSpeedStat
    /// (RecipeDef.ResolveReferences defaults it to WorkTableWorkSpeedFactor). A recipe gated by ideology, or whose slot no def fills,
    /// is left out.
    /// </summary>
    public IReadOnlyList<P.StockRecipe> StockRecipes(IReadOnlySet<string> finished) =>
        [.. StockRecipeRows().Where(r => r.Research.All(finished.Contains)).Select(r => r.Recipe)];

    // The catalog's recipes do not change: the stock recipe rows are read once per catalog, research judged per call.
    private IReadOnlyList<StockRecipeRow> StockRecipeRows() => LazyInitializer.EnsureInitialized(ref _stockRecipes, () =>
    {
        var parts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var install in Rows<RecipeDef>().Values.Where(r => r.workerClass is { } worker
            && (typeof(Recipe_InstallNaturalBodyPart).IsAssignableFrom(worker) || typeof(Recipe_InstallArtificialBodyPart).IsAssignableFrom(worker))))
        {
            foreach (var slot in install.ingredients.Seq().Where(i => i.IsFixedIngredient && i.FixedIngredient is { } part && !Medicine(part.defName)))
            {
                parts.Add(slot.FixedIngredient.defName);
            }
        }

        var rows = new List<StockRecipeRow>();
        foreach (var row in Rows<RecipeDef>().Values.OrderBy(r => r.defName, StringComparer.Ordinal))
        {
            if (row.memePrerequisitesAny.Seq().Any() || row.factionPrerequisiteTags.Seq().Any() || row.fromIdeoBuildingPreceptOnly
                || row.products.Seq().FirstOrDefault() is not { thingDef: { } product } made
                || !(Medicine(product.defName) || parts.Contains(product.defName)) || !RecipeFillable(row.defName))
            {
                continue;
            }

            var benches = new List<P.StockRecipeBench>();
            foreach (var bench in RecipeBenchesOf(row))
            {
                if (BillWorkType(row, bench) is { } work && TryWorkType(work, out var type))
                {
                    benches.Add(new P.StockRecipeBench(bench, type));
                }
            }

            if (benches.Count == 0)
            {
                continue;
            }

            if (StockRecipeOf(row, product, made.count, benches) is { } recipe)
            {
                rows.Add(new StockRecipeRow(recipe, [.. RecipeResearch(row)]));
            }
        }

        return rows;
    });

    // The stock recipe row of the recipe; null when the catalog cannot read its facts, as CraftRowOf leaves such a recipe out.
    private P.StockRecipe? StockRecipeOf(RecipeDef row, ThingDef product, int batch, List<P.StockRecipeBench> benches)
    {
        try
        {
            return new P.StockRecipe
            {
                Recipe = row.defName,
                Product = new P.Resource(product.defName),
                Batch = batch,
                WorkAmount = RecipeWorkAmount(row.defName),
                WorkSpeedStat = RecipeWorkSpeedStat(row.defName),
                BenchSpeedStat = row.workTableSpeedStat?.defName ?? nameof(StatDefOf.WorkTableWorkSpeedFactor),
                Benches = EquatableArray.From(benches),
                Skills = EquatableArray.From(row.skillRequirements.Seq().Select(s => new P.SkillRequirement(s.skill.defName, s.minLevel))),
                Ingredients = RecipeIngredients(row.defName),
            };
        }
        catch (Exception e) when (e is DomainException or CatalogException)
        {
            return null;
        }
    }
}
