using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_shells.go and catalog_ledger_kind.go.

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// The load's mortar shells by kind: every thing def with a projectileWhenLoaded (the game's <c>ThingDef.IsShell</c>)
    /// is classified by the damage def of that projectile, never by its name. EMP: the damage stuns and applies to
    /// mechanoids. Incendiary: the projectile is incendiary (ai_IsIncendiary) and the damage harms health. HE: the damage
    /// is explosive, harms health and has an armor category, and the blast stays inside <paramref name="safeRadius"/>, the
    /// radius at which a shot would reach our own: a shell that big (the antigrain warhead) is not one the
    /// counter-battery fires. A shell that is none of those (smoke, firefoam, tox, deadlife) is skipped, and so is a kind
    /// more than one shell qualifies for: a kind is never guessed, and its def is "". A shell whose projectile or damage
    /// def has no row is an error.
    /// </summary>
    public P.MortarShells MortarShells(double safeRadius)
    {
        var he = new List<string>();
        var incendiary = new List<string>();
        var emp = new List<string>();
        foreach (var (name, row) in _thingDefs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (row.projectileWhenLoaded is not { } loaded)
            {
                continue;
            }

            var properties = ThingRow(loaded.defName).projectile
                ?? throw new CatalogException($"catalog def {loaded.defName} is the projectile of shell {name} but has no projectile properties");
            var damage = properties.damageDef ?? throw new CatalogException($"catalog has no damage def row for the damage of shell {name}");
            if (damage.causeStun && GameMembers.ExternalViolenceForMechanoids(damage))
            {
                emp.Add(name);
            }
            else if (damage.harmsHealth && properties.ai_IsIncendiary)
            {
                incendiary.Add(name);
            }
            else if (damage.isExplosive && damage.harmsHealth && damage.armorCategory is not null && properties.explosionRadius < safeRadius)
            {
                he.Add(name);
            }
        }

        static string Only(List<string> defs) => defs.Count == 1 ? defs[0] : string.Empty;
        return new P.MortarShells { HE = Only(he), Incendiary = Only(incendiary), EMP = Only(emp) };
    }

    /// <summary>
    /// The work ledger's declare-only class of a recipe by what it makes: a mech gestation (<paramref name="mechKind"/> is
    /// the recipe's RecipeMechKind), medicine, a body part item or food a baby can ingest; production for every other
    /// recipe. A bill of a declare-only class is never removed as an orphan in the first pass of the ledger (#2604), so
    /// the class is read from the catalog row alone and is the same after a restart. A recipe with no row is an error.
    /// </summary>
    public P.LedgerBillKind RecipeLedgerKind(string name, string mechKind)
    {
        if (mechKind.Length > 0)
        {
            return P.LedgerBillKind.MechGestation;
        }

        var products = Recipe(name).products.Seq().ToList();
        var baby = products.Count > 0;
        foreach (var product in products)
        {
            if (product.thingDef is not { } def)
            {
                baby = false;
                continue;
            }

            if (Medicine(def.defName))
            {
                return P.LedgerBillKind.Medical;
            }

            var thing = ThingRow(def.defName);
            if (CategoriesWithin(thing).Contains(CategoryBodyParts))
            {
                return P.LedgerBillKind.Surgery;
            }

            baby = baby && thing.ingestible is { babiesCanIngest: true };
        }

        return baby ? P.LedgerBillKind.BabyFood : P.LedgerBillKind.Production;
    }
}
