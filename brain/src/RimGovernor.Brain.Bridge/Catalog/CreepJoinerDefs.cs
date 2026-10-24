using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The creepjoiner planner's def lookups. They read the defs and name no def.

public sealed partial class DefinitionCatalog
{
    /// <summary>The recipe defs whose worker is the game's surgical inspection (Recipe_SurgicalInspection); none without Anomaly.</summary>
    public IReadOnlySet<string> SurgicalInspectionRecipes()
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, recipe) in Rows<RecipeDef>())
        {
            if (recipe.workerClass is not null && typeof(Recipe_SurgicalInspection).IsAssignableFrom(recipe.workerClass))
            {
                output.Add(name);
            }
        }
        return output;
    }

    /// <summary>Whether the game runs Anomaly: the defs carry EntityCategoryDef rows only with the expansion.</summary>
    public bool HasAnomaly() => Rows<EntityCategoryDef>().Count > 0;

    /// <summary>What the creepjoiner downside defs add: the trait and hediff def names over every CreepJoinerDownsideDef row. A game without Anomaly adds none.</summary>
    public P.CreepJoinerDownsides CreepJoinerDownsides()
    {
        var traits = new HashSet<string>(StringComparer.Ordinal);
        var hediffs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in Rows<CreepJoinerDownsideDef>().Values)
        {
            foreach (var t in def.traits.Seq())
            {
                traits.Add(t.def.defName);
            }
            foreach (var h in def.hediffs.Seq())
            {
                hediffs.Add(h.defName);
            }
        }
        return new P.CreepJoinerDownsides { Traits = traits, Hediffs = hediffs };
    }

    /// <summary>
    /// The downside defs the posterior draws over and the agony pulse (#3844, P.ForecastIsolation): every
    /// CreepJoinerDownsideDef row by name, the forms and benefits whose requires list names a drawable downside, and the
    /// pulse when some downside's worker is CreepJoinerWorker_PsychicAgony. Empty without Anomaly. A pulse the game renames
    /// or retypes is an error naming it.
    /// </summary>
    public P.CreepJoinerDownsideTable CreepJoinerDownsideTable() =>
        _downsideTable ?? LazyInitializer.EnsureInitialized(ref _downsideTable, ReadCreepJoinerDownsideTable);

    // The defs and the worker's constants are fixed for a catalog, so it reads them once.
    private P.CreepJoinerDownsideTable? _downsideTable;

    private P.CreepJoinerDownsideTable ReadCreepJoinerDownsideTable()
    {
        static (double, double) Range(FloatRange r) => (Decimal32(r.min), Decimal32(r.max));
        var downsides = Rows<CreepJoinerDownsideDef>().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToList();
        var rows = downsides.Select(d => new P.CreepJoinerDownsideRow
        {
            Def = d.defName,
            Weight = Decimal32(d.Weight),
            CanOccurRandomly = d.CanOccurRandomly,
            MinCombatPoints = Decimal32(d.MinCombatPoints),
            AddsTrait = d.traits.Seq().Any(),
            TriggersAfterDays = Range(d.triggersAfterDays),
            TriggerMtbDays = Decimal32(d.triggerMtbDays),
            TriggerMinDays = Range(d.triggerMinDays),
            PsychicAgony = d.workerType is { } worker && typeof(CreepJoinerWorker_PsychicAgony).IsAssignableFrom(worker),
        }).ToList();
        // GetRandom narrows the draw to the drawable defs a requires list names when one qualifies (see P.ForecastIsolation).
        var drawable = downsides.Where(d => d.CanOccurRandomly).ToHashSet();
        var requiring = Rows<CreepJoinerFormKindDef>().Values.Where(f => f.Requires.Seq().Any(r => r is CreepJoinerDownsideDef d && drawable.Contains(d))).Select(f => f.defName)
            .Concat(Rows<CreepJoinerBenefitDef>().Values.Where(b => b.Requires.Seq().Any(r => r is CreepJoinerDownsideDef d && drawable.Contains(d))).Select(b => b.defName))
            .Order(StringComparer.Ordinal).ToList();
        return new P.CreepJoinerDownsideTable
        {
            Downsides = EquatableArray.From(rows),
            Agony = rows.Any(r => r.PsychicAgony) ? ReadAgonyPulse() : null,
            Unmirrored = requiring.Count == 0 ? "" : "the requires lists of " + string.Join(", ", requiring) + " name a downside",
        };
    }

    private P.AgonyPulse ReadAgonyPulse()
    {
        // CreepJoinerWorker_PsychicAgony keeps its reach and duration in a private const and a private static readonly with no
        // accessor, so they are read by name; it gives HediffDefOf.AgonyPulse.
        var name = nameof(HediffDefOf.AgonyPulse);
        var hediff = DefRow<HediffDef>(name) ?? throw new CatalogException($"the catalog has no {name} hediff def");
        var rate = hediff.comps.Seq().OfType<HediffCompProperties_SeverityPerSecond>().FirstOrDefault()
            ?? throw new CatalogException($"{name} has no SeverityPerSecond comp");
        var seconds = GameConstants.Read<IntRange>(typeof(CreepJoinerWorker_PsychicAgony), "DisappearSeconds");
        return new P.AgonyPulse
        {
            Range = Decimal32(GameConstants.Read<float>(typeof(CreepJoinerWorker_PsychicAgony), "Range")),
            Table = PenaltyTable(name),
            MaxSeverity = Decimal32(hediff.maxSeverity),
            SeverityPerSecond = (Decimal32(rate.severityPerSecond + rate.severityPerSecondRange.min), Decimal32(rate.severityPerSecond + rate.severityPerSecondRange.max)),
            DisappearsTicks = (seconds.min * P.ForecastIsolation.TicksPerSecond, seconds.max * P.ForecastIsolation.TicksPerSecond),
        };
    }
}
