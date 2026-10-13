using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_traits.go, catalog_trait_balance.go and catalog_thought_facts.go: what a trait degree does, what it is
// worth, and the thought facts of the mood ledger. A float the XML wrote as 0.2 is read as 0.2, not 0.20000000298
// (Decimal32).

public sealed partial class DefinitionCatalog
{
    // The stats a trait's offsets are read through (the game's StatDef names).
    private const string StatWorkSpeed = "WorkSpeedGlobal";
    private const string StatLearningFactor = "GlobalLearningFactor";
    private const string StatMoveSpeed = "MoveSpeed";
    private const string StatRestRate = "RestRateMultiplier";
    private const string StatShooting = "ShootingAccuracyPawn";

    // The stats whose trait offset or factor marks a pawn who fights on the melee line: more melee hits or dodges, less
    // incoming damage.
    private const string StatMeleeHit = "MeleeHitChance";
    private const string StatMeleeDodge = "MeleeDodgeChance";
    private const string StatIncomingDamage = "IncomingDamageFactor";

    // The NeedDef a trait disables to keep its pawn out of the open (Outdoors).
    private const string NeedOutdoors = "Outdoors";

    // requiredTraitsDegree of a thought that requires its trait at any degree (the field's unset value).
    private const int NoRequiredDegree = int.MinValue;

    private TraitDef TraitRow(string name) => RequireRow<TraitDef>(name);

    private static TraitDegreeData TraitDegreeDataOf(TraitDef row, int degree, string name) =>
        row.degreeDatas.Seq().FirstOrDefault(entry => entry.degree == degree)
        ?? throw new CatalogException($"catalog trait {name} has no degree {degree}");

    /// <summary>
    /// What a pawn's trait of the given degree does, read from the TraitDef row: the summed WorkSpeedGlobal,
    /// GlobalLearningFactor and MoveSpeed offsets of the degree, the work types its disabled work tags (and disabled work
    /// types) take away, a RestRateMultiplier offset above zero as a quick sleeper, the Outdoors need it disables as an
    /// undergrounder, and the human-meat ingestion thoughts it disallows as a cannibal. The thoughts it nullifies or is
    /// required by give Execution, HumanButcher, Nudist and MeleeOnly, and a ShootingAccuracyPawn offset RearRanged. A trait
    /// or degree the catalog lacks is an error; effects the game applies in code and no row states come from
    /// <c>PolicyFunctions.TraitSociable</c>, and a hunger-rate factor above 1 is a gourmand.
    /// </summary>
    public P.TraitEffects TraitEffects(string name, int degree)
    {
        var row = TraitRow(name);
        var data = TraitDegreeDataOf(row, degree, name);
        double workSpeed = 0, learnRate = 0, moveSpeed = 0, rest = 0;
        var rearRanged = false;
        var frontLine = false;
        foreach (var entry in data.statOffsets.Seq())
        {
            var offset = Decimal32(entry.value);
            switch (entry.stat.defName)
            {
                case StatWorkSpeed:
                    workSpeed += offset;
                    break;
                case StatLearningFactor:
                    learnRate += offset;
                    break;
                case StatMoveSpeed:
                    moveSpeed += offset;
                    break;
                case StatRestRate:
                    rest += offset;
                    break;
                case StatShooting:
                    rearRanged |= offset != 0;
                    break;
                case StatMeleeHit or StatMeleeDodge:
                    frontLine |= offset > 0;
                    break;
            }
        }

        foreach (var entry in data.statFactors.Seq())
        {
            if (entry.stat.defName == StatIncomingDamage && Decimal32(entry.value) < 1)
            {
                frontLine = true;
            }
        }

        bool Listed(string thought, bool required)
        {
            var def = RequireRow<ThoughtDef>(thought);
            return required
                ? def.requiredTraits.Seq().Any(t => t.defName == name)
                : def.nullifyingTraits.Seq().Any(t => t.defName == name);
        }

        var undergrounder = data.disablesNeeds.Seq().Any(need => need.defName == NeedOutdoors);
        var cannibal = data.disallowedThoughtsFromIngestion.Seq().Any(entry => entry.meatSource == MeatSourceCategory.Humanlike);
        var disabled = Rows<WorkTypeDef>().Values
            .Where(work => (row.disabledWorkTags & work.workTags) != 0 || row.disabledWorkTypes.Seq().Contains(work))
            .Select(work => work.defName)
            .Order(StringComparer.Ordinal)
            .Select(name => TryWorkType(name, out var type) ? type : P.WorkType.None)
            .Where(type => type != P.WorkType.None)
            .ToList();
        return new P.TraitEffects
        {
            WorkSpeed = workSpeed,
            LearnRate = learnRate,
            MoveSpeed = moveSpeed,
            RearRanged = rearRanged,
            FrontLine = frontLine,
            Execution = Listed(ThoughtPrisonerDied, false),
            HumanButcher = Listed(ThoughtButcheredHuman, false),
            Nudist = Listed(ThoughtNaked, false),
            MeleeOnly = Listed(ThoughtRangedCarried, true),
            SurgeonSafe = Listed(ThoughtOrganHarvested, false),
            TaintFree = Listed(ThoughtTaintedApparel, false),
            Greedy = Listed(ThoughtGreedy, true),
            Ascetic = Listed(ThoughtAscetic, true),
            Jealous = Listed(ThoughtJealous, true),
            NightShift = Listed(ThoughtNightOwl, true),
            Pyromaniac = Listed(ThoughtPyromaniac, true),
            ChemicalInterest = RequireRow<ThoughtDef>(ThoughtDrugDesire).requiredTraits.Seq().Any(t => t.defName == name) ? degree : 0,
            QuickSleeper = rest > 0,
            Undergrounder = undergrounder,
            Cannibal = cannibal,
            DisabledWork = disabled,
            Gourmand = data.hungerRateFactor > 1,
            Sociable = P.PolicyFunctions.TraitSociable(name),
        };
    }

    /// <summary>
    /// What a trait degree does to the terms a prospect is valued by (<c>TraitBalance.Worth</c> weighs them): the stat
    /// offsets and the distance of each stat factor from 1, the hunger-rate factor above 1, the work types it disables, its
    /// code-applied sociability, and its mood: the first stage of each always-active thought that requires the trait at
    /// this degree, and the worst stage of each situational one when that is negative. A trait or degree the catalog lacks
    /// is an error.
    /// </summary>
    public P.TraitBalance TraitBalance(string name, int degree)
    {
        var data = TraitDegreeDataOf(TraitRow(name), degree, name);
        var effects = TraitEffects(name, degree);
        var stats = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in data.statOffsets.Seq())
        {
            stats[entry.stat.defName] = stats.GetValueOrDefault(entry.stat.defName) + Decimal32(entry.value);
        }

        foreach (var entry in data.statFactors.Seq())
        {
            stats[entry.stat.defName] = stats.GetValueOrDefault(entry.stat.defName) + Decimal32(entry.value) - 1;
        }

        double mood = 0;
        foreach (var thought in Rows<ThoughtDef>().Values.OrderBy(t => t.defName, StringComparer.Ordinal))
        {
            if (!thought.requiredTraits.Seq().Any(t => t.defName == name)
                || (thought.requiredTraitsDegree != NoRequiredDegree && thought.requiredTraitsDegree != degree)
                || !thought.stages.Seq().Any())
            {
                continue;
            }

            // The ThoughtDef worker of a thought that holds for as long as the pawn has the trait (the NaturalMood
            // offsets): its first stage is a permanent mood offset. Any other worker's thought is situational.
            if (thought.workerClass is { } worker && typeof(ThoughtWorker_AlwaysActive).IsAssignableFrom(worker))
            {
                mood += Decimal32(thought.stages[0].baseMoodEffect);
            }
            else
            {
                mood += Math.Min(0, thought.stages.Where(stage => stage is not null).Select(stage => Decimal32(stage.baseMoodEffect)).DefaultIfEmpty(0).Min());
            }
        }

        return new P.TraitBalance
        {
            Stats = stats,
            DisabledWork = effects.DisabledWork.Count,
            Sociable = effects.Sociable,
            HungerRate = data.hungerRateFactor > 1 ? Decimal32(data.hungerRateFactor) - 1 : 0,
            Mood = mood,
        };
    }

    /// <summary>The named ThoughtDef row and the policy audit table projected into the fact the mood ledger reads; null for a def the catalog lacks. A def the audit table lacks is projected with the unclassified dependency and no owner. Null stages (the game keeps unused ones null) are empty stages.</summary>
    public P.ThoughtFacts? ThoughtFacts(string name)
    {
        if (DefRow<ThoughtDef>(name) is not { } row)
        {
            return null;
        }

        var stages = new List<P.MoodStage>();
        double worst = 0;
        var first = true;
        foreach (var stage in row.stages.Seq())
        {
            if (stage is null)
            {
                stages.Add(default);
                continue;
            }

            var offset = Decimal32(stage.baseMoodEffect);
            stages.Add(new P.MoodStage(offset, true));
            if (first || offset < worst)
            {
                (worst, first) = (offset, false);
            }
        }

        var facts = new P.ThoughtFacts
        {
            Def = name,
            NullifyingTraits = [.. row.nullifyingTraits.Seq().Select(t => t.defName)],
            NullifyingPrecepts = [.. row.nullifyingPrecepts.Seq().Select(p => p.defName)],
            RequiredTraits = [.. row.requiredTraits.Seq().Select(t => t.defName)],
            MinExpectation = row.minExpectation?.defName ?? string.Empty,
            Dependency = ThoughtUnclassified,
            Stages = stages,
            WorstOffset = worst,
        };
        return P.PolicyFunctions.ThoughtAuditRow(name) is ({ } audit, true) ? facts with { Dependency = audit.Dependency, Owner = audit.Owner } : facts;
    }

    /// <summary><see cref="ThoughtFacts"/> for every ThoughtDef row, by defName.</summary>
    public IReadOnlyDictionary<string, P.ThoughtFacts> AllThoughtFacts() =>
        Rows<ThoughtDef>().Keys.ToDictionary(name => name, name => ThoughtFacts(name)!, StringComparer.Ordinal);
}
