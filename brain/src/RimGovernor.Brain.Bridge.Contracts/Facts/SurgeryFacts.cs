using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// The catalog answers surgery facts read per recipe and hediff: the part item a recipe installs, its tier and the
/// item a hediff spawns on removal. The bridge definition catalog implements it; the port keeps the dependency a
/// seam because the catalog derivations live in their own project. Every method throws when the catalog has no row
/// for the name.
/// </summary>
public interface IPartCatalog
{
    /// <summary>The part item the recipe installs; default when it installs none.</summary>
    Resource InstallItem(string recipe);

    /// <summary>The recipe's installed part tier.</summary>
    double PartTier(string recipe);
}

public static class SurgeryFactsRules
{
    /// <summary>
    /// Each native operation with the part item its recipe installs and that part's tier, read from the catalog's
    /// recipe rows; a recipe the catalog has no row for throws. Values are never recomputed here.
    /// </summary>
    public static EquatableArray<CareOperation> CareOperations(PawnSurgery surgery, IPartCatalog catalog) =>
        EquatableArray.From(
            surgery.Operations.Select(op => new CareOperation { Operation = op, Item = catalog.InstallItem(op.Recipe), Tier = catalog.PartTier(op.Recipe) })
        );

    /// <summary>
    /// Lists the part item each queued medical bill installs, by the catalog's
    /// recipe rows; a bill that installs no item adds none.
    /// </summary>
    public static IReadOnlyList<Resource> QueuedSurgeryItems(IEnumerable<string> recipes, IPartCatalog catalog)
    {
        var items = new List<Resource>();
        foreach (var recipe in recipes)
        {
            var item = catalog.InstallItem(recipe);
            if (item != default)
            {
                items.Add(item);
            }
        }
        return items;
    }

    /// <summary>Each medical bill's recipe on the patient.</summary>
    public static IReadOnlyList<string> QueuedSurgeryRecipes(PawnSurgery surgery) =>
        [.. surgery.SurgeryBills.Where(bill => bill.Recipe.Length > 0).Select(bill => bill.Recipe)];
}
