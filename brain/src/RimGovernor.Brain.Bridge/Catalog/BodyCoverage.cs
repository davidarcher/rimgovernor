using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The hit-location odds of the human body and the apparel layer order of the harm model's armor response (#3540).

public sealed partial class DefinitionCatalog
{
    /// <summary>
    /// The Human <c>BodyDef</c> walked as <c>SurgeryParts</c> walks it: every part's <c>coverageAbs</c> summed per distinct set
    /// of apparel body-part groups the part belongs to (parts in no group are the uncovered remainder), and the apparel
    /// layers ordered by <c>drawOrder</c>.
    /// </summary>
    public P.BodyCoverage BodyCoverage() => _bodyCoverage.Value;

    /// <summary>
    /// The Human race's own armor ratings (<c>ArmorRating_Sharp/Blunt/Heat</c> of <c>ThingDefOf.Human</c>), the innermost layer of
    /// every hit; a rating the race does not show is zero.
    /// </summary>
    public P.NaturalArmor NaturalArmor() => _naturalArmor.Value;

    private P.NaturalArmor BuildNaturalArmor()
    {
        double Rating(CatalogStat stat)
        {
            var (value, shown) = ShownStatValue(nameof(ThingDefOf.Human), string.Empty, stat);
            return shown ? Math.Max(0, (double)value) : 0;
        }

        return new P.NaturalArmor { Sharp = Rating(CatalogStats.ArmorSharp), Blunt = Rating(CatalogStats.ArmorBlunt), Heat = Rating(CatalogStats.ArmorHeat) };
    }

    private P.BodyCoverage BuildBodyCoverage()
    {
        // BodyDefOf.Human names the row; the field itself is null outside the game, so the catalog looks it up by its nameof.
        var body = RequireRow<BodyDef>(nameof(BodyDefOf.Human));
        var sums = new Dictionary<string, (string[] Groups, double Probability)>(StringComparer.Ordinal);
        var stack = new Stack<BodyPartRecord>();
        stack.Push(body.corePart);
        while (stack.Count > 0)
        {
            var record = stack.Pop();
            var groups = record.groups.Seq().Select(g => g.defName).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (groups.Length > 0)
            {
                var key = string.Join('|', groups);
                sums[key] = (groups, sums.GetValueOrDefault(key).Probability + Decimal32(record.coverageAbs));
            }

            foreach (var child in record.parts.Seq())
            {
                stack.Push(child);
            }
        }

        return new P.BodyCoverage
        {
            Shares = Domain.EquatableArray.From(sums.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new P.CoverageShare { Groups = Domain.EquatableArray.From(s.Value.Groups), Probability = s.Value.Probability })),
            Layers = Domain.EquatableArray.From(Rows<ApparelLayerDef>().Values.OrderBy(l => l.drawOrder).ThenBy(l => l.defName, StringComparer.Ordinal).Select(l => l.defName)),
        };
    }
}
