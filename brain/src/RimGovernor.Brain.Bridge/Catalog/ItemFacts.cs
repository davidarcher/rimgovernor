using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_item_facts.go: the planner-facing item numbers of the catalog.

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// The planner-facing item numbers of the catalog: the game's market value and nutrition of every item def, the
    /// potency of every medicine, the stuff factors, the currency and the drugs. Categories carry every thing category a
    /// def sits within, parents included. A stuff with no market value is an error, since it could not be priced.
    /// </summary>
    public P.ItemFacts ItemFacts() => _items.Value;

    private P.ItemFacts BuildItemFacts()
    {
        var market = new Dictionary<P.Resource, double>();
        var nutrition = new Dictionary<P.Resource, double>();
        var medicalPotency = new Dictionary<P.Resource, double>();
        var stuffBeauty = new Dictionary<P.Resource, double>();
        var stuffCategories = new Dictionary<P.Resource, IReadOnlyList<string>>();
        var acceptedStuff = new Dictionary<P.Resource, IReadOnlyList<string>>();
        var categories = new Dictionary<P.Resource, IReadOnlyList<string>>();
        var deepResources = new List<P.Resource>();
        var armor = new List<P.Resource>();
        foreach (var def in _thingDefs.Values.OrderBy(d => d.defName, StringComparer.Ordinal))
        {
            var resource = new P.Resource(def.defName);
            if (def.stuffCategories.Seq().Any() is false)
            {
                var (value, shown) = ShownStatValue(def.defName, string.Empty, StatNames.MarketValue);
                if (shown)
                {
                    market[resource] = value;
                }

                (value, shown) = ShownStatValue(def.defName, string.Empty, StatNames.Nutrition);
                if (shown)
                {
                    nutrition[resource] = value;
                }
            }

            if (def.thingCategories.Seq().Any())
            {
                categories[resource] = [.. CategoriesWithin(def).Order(StringComparer.Ordinal)];
            }

            if (def.stuffCategories.Seq().Any())
            {
                acceptedStuff[resource] = [.. def.stuffCategories.Select(c => c.defName)];
            }

            if (def.deepCommonality > 0)
            {
                deepResources.Add(resource);
            }

            if (def.apparel is { } apparel && ApparelIsArmor(apparel))
            {
                armor.Add(resource);
            }

            // A medicine is a def whose base stats carry a medical potency (ThingDef.IsMedicine).
            foreach (var mod in def.statBases.Seq().Where(m => m.stat.defName == StatNames.MedicalPotency))
            {
                medicalPotency[resource] = mod.value;
            }

            if (def.stuffProps is { } props)
            {
                stuffCategories[resource] = [.. props.categories.Seq().Select(c => c.defName)];
                stuffBeauty[resource] = StuffFactor(props, StatNames.Beauty);
                if (!market.ContainsKey(resource))
                {
                    throw new CatalogException($"catalog stuff {def.defName} has no market value");
                }
            }
        }

        var items = new P.ItemFacts
        {
            Market = market,
            Nutrition = nutrition,
            MedicalPotency = medicalPotency,
            StuffBeauty = stuffBeauty,
            StuffCategories = stuffCategories,
            AcceptedStuff = acceptedStuff,
            Categories = categories,
            DeepResources = [.. deepResources],
            Armor = [.. armor],
            Currency = new P.Resource(Derived.CurrencyDef),
            Wort = new P.Resource(Derived.WortDef),
        };
        return BuildDrugFacts(items) with { Sculptures = Sculptures() };
    }

    /// <summary>
    /// The apparel policy's armor rule on a def's apparel properties: only the game's Soldier outfit tag names it, not
    /// Worker too (the Soldier outfit wears it, the Worker outfit does not). The rule reads
    /// <c>ApparelProperties.defaultOutfitTags</c>, the tags the game's own Worker and Soldier outfit filters select
    /// apparel by (<c>RimWorld.OutfitDatabase.GenerateStartingOutfits</c>).
    /// </summary>
    /// <remarks>
    /// Checked against every non-abstract apparel ThingDef of Core, Royalty, Ideology, Biotech, Anomaly and Odyssey (112
    /// defs, 31 armor). Soldier without Worker holds exactly the flak, plate, recon, power, marine, cataphract and
    /// locust armors and their helmets, the war mask and veil, the kid helmet, and the Royalty psyfocus gear and eltex
    /// skullcap/gunlink. Parka, tuque, shirts, pants and tribal wear carry Soldier and Worker, so they are clothing;
    /// untagged and Worker-only defs are clothing as well.
    /// </remarks>
    public static bool ApparelIsArmor(ApparelProperties apparel)
    {
        var tags = apparel.defaultOutfitTags.Seq().ToList();
        return tags.Contains("Soldier") && !tags.Contains("Worker");
    }

    // StuffFactor is the stuff's stat factor, 1 when it has none.
    private static double StuffFactor(StuffProperties props, string stat) =>
        props.statFactors.Seq().FirstOrDefault(m => m.stat.defName == stat)?.value ?? 1;
}
