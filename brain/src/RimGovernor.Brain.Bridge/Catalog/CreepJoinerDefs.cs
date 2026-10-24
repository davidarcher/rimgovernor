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
        var rows = downsides.Select(d => (d, Triggered: TriggeredHediffs(d).ToList())).Select(x => new P.CreepJoinerDownsideRow
        {
            Def = x.d.defName,
            Weight = Decimal32(x.d.Weight),
            CanOccurRandomly = x.d.CanOccurRandomly,
            MinCombatPoints = Decimal32(x.d.MinCombatPoints),
            AddsTrait = x.d.traits.Seq().Any(),
            TriggersAfterDays = Range(x.d.triggersAfterDays),
            TriggerMtbDays = Decimal32(x.d.triggerMtbDays),
            TriggerMinDays = Range(x.d.triggerMinDays),
            Repeats = x.d.repeats,
            Response = ResponseOf(x.d.workerType),
            Hediffs = EquatableArray.From(x.Triggered.Where(t => t.Hediff is not null).Select(t => t.Hediff!).ToArray()),
            Unmodelled = EquatableArray.From(x.Triggered.Select(t => t.Unmodelled).Where(u => u.Length != 0)
                .Concat(x.d.workerType is { } w && ResponseOf(w) == P.CreepJoinerResponse.Other ? new[] { "creepjoiner_downside_response_unmodelled:" + x.d.defName + ":" + w.Name } : Array.Empty<string>())
                .Concat(x.d.abilities.Seq().Any() ? new[] { "creepjoiner_downside_abilities_unmodelled:" + x.d.defName } : Array.Empty<string>())
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
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

    // The worker classes the forecast prices (#3844 D12): DoResponse of each is read with ilspycmd (see P.CreepJoinerHarm).
    private static P.CreepJoinerResponse ResponseOf(Type? worker) => worker switch
    {
        null => P.CreepJoinerResponse.None,
        _ when typeof(CreepJoinerWorker_PsychicAgony).IsAssignableFrom(worker) => P.CreepJoinerResponse.PsychicAgony,
        _ when typeof(CreepJoinerWorker_AggressiveDownside).IsAssignableFrom(worker) => P.CreepJoinerResponse.Aggressive,
        _ when typeof(CreepJoinerWorker_DepartDownside).IsAssignableFrom(worker) => P.CreepJoinerResponse.Leaves,
        _ => P.CreepJoinerResponse.Other,
    };

    // The hediffs a downside's firing gives (#3844 D12): DoDownside calls HediffComp_ReplaceHediff.Trigger on each of the downside's
    // hediffs the pawn carries (the downside's own hediffs, added when it was drawn), which applies each TriggeredHediff. A downside
    // hediff without a manually triggered ReplaceHediff, or one whose ReplaceHediff fires on its severity, is named.
    private IEnumerable<(P.CreepJoinerHediff? Hediff, string Unmodelled)> TriggeredHediffs(CreepJoinerDownsideDef d)
    {
        foreach (var carried in d.hediffs.Seq().Where(h => h is not null))
        {
            var replace = carried.comps.Seq().OfType<HediffCompProperties_ReplaceHediff>().ToArray();
            if (replace.Length != 1 || !replace[0].manuallyTriggered)
            {
                yield return (null, "creepjoiner_downside_hediff_unmodelled:" + d.defName + ":" + carried.defName);
                continue;
            }

            foreach (var t in replace[0].hediffs.Seq().Where(t => t?.hediff is not null))
            {
                yield return (Triggered(t, 0), "");
            }
        }
    }

    // One TriggeredHediff as a course, with the hediff its own ReplaceHediff gives once its severity reaches the comp's severity
    // (a chain the course follows, e.g. CrumblingMind into CrumbledMind). A chain deeper than the defs allow is an error.
    private P.CreepJoinerHediff Triggered(HediffCompProperties_ReplaceHediff.TriggeredHediff t, int depth)
    {
        if (depth > 4) throw new CatalogException("the creepjoiner hediff chain from " + t.hediff.defName + " does not end");
        var course = DiseaseCourse(t.hediff, t.partsToAffect, DefRow<BodyDef>(nameof(BodyDefOf.Human)));
        var next = t.hediff.comps.Seq().OfType<HediffCompProperties_ReplaceHediff>().FirstOrDefault();
        P.CreepJoinerHediff? then = null;
        var unmodelled = course.Unmodelled.ToList();
        // D12 prices the hediff's capacity loss; the work its stages disable (CrumbledMind's Caring, ManualDumb, Intellectual,
        // ManualSkilled) is named.
        if (t.hediff.stages.Seq().Any(s => s.disabledWorkTags != WorkTags.None))
        {
            unmodelled.Add("disabledWorkTags");
        }
        if (next is { manuallyTriggered: false, hediffs.Count: 1 } && next.hediffs[0]?.hediff is not null
            && t.hediff.comps.Seq().OfType<HediffCompProperties_ReplaceHediff>().Count() == 1)
        {
            // The chained replacement is followed, so its comp is modelled.
            then = Triggered(next.hediffs[0], depth + 1);
            unmodelled.Remove(nameof(HediffCompProperties_ReplaceHediff));
        }

        return new P.CreepJoinerHediff
        {
            Course = course with { Unmodelled = [.. unmodelled] },
            Count = (t.countRange.min, t.countRange.max),
            Severity = (Decimal32(t.severityRange.min), Decimal32(t.severityRange.max)),
            Then = then,
            ThenAt = next is null ? 0 : Decimal32(next.severity),
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
