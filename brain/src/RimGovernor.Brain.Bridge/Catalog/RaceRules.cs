using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go race_rules.go and the mech kind of biotech.go (the parts the catalog reads): the race rules the game's code
// states over def fields (RaceProperties, FleshTypeDef, LifeStageDef, TrainableDef, TrainabilityDef). What the game's
// code computes beyond its fields stays in RaceGameFacts: tameness decay, the wildness curve, the adult feed and the
// edible foods. The rules compare flesh types by def name, as the game compares them to its FleshTypeDefOf statics,
// so they hold in a host whose DefOf statics are not bound.

/// <summary>The husbandry ages of a race in ticks: the first stage (life stage order) whose LifeStageDef is reproductive, milkable or shearable, null when none is.</summary>
internal sealed record RaceStageAges(long Adult, long? Reproductive, long? Milkable, long? Shearable);

public sealed partial class DefinitionCatalog
{
    private const string FleshNormal = "Normal";
    private const string FleshMechanoid = "Mechanoid";
    private const string FleshInsectoid = "Insectoid";

    // The flesh types RaceProperties.IsAnomalyEntity names; the defs exist only with Anomaly, so no race of another
    // game refers to them.
    private static readonly string[] AnomalyFleshTypes = ["EntityMechanical", "EntityFlesh", "Fleshbeast"];

    /// <summary>The Nutrition-stat name of a butchery's meat yield.</summary>
    public const string StatMeatAmount = "MeatAmount";

    // RaceFleshType is RaceProperties.FleshType's name: a race with none is Normal.
    private static string RaceFleshType(RaceProperties props) => GameMembers.FleshType(props)?.defName ?? FleshNormal;

    /// <summary>The mechanoid and insect flags of def's race (<c>RaceProperties.IsMechanoid</c> and <c>.Insect</c>); false for a def without RaceProperties.</summary>
    public (bool Mechanoid, bool Insect) RaceFlags(string def)
    {
        if (FindThing(def)?.race is not { } props)
        {
            return (false, false);
        }

        var flesh = RaceFleshType(props);
        return (flesh == FleshMechanoid, flesh == FleshInsectoid);
    }

    // RaceIsAnimal is RaceProperties.Animal: no tool use, organic flesh, and no Anomaly entity.
    private bool RaceIsAnimal(string def, RaceProperties props)
    {
        if (props.intelligence >= Intelligence.ToolUser)
        {
            return false;
        }

        var flesh = RaceFleshType(props);
        var row = DefRow<FleshTypeDef>(flesh) ?? throw new CatalogException($"race {def} has flesh type {flesh} with no row");
        return row.isOrganic && !AnomalyFleshTypes.Contains(flesh);
    }

    // RaceMeatDef is RaceProperties.meatDef, an unsaved field the game resolves from its defs (specificMeatDef, else
    // the meat of useMeatFrom, else the meat ThingDefGenerator_Meat makes): empty for a race with no meat.
    private static string RaceMeatDef(ThingDef row) => row.race?.meatDef?.defName ?? string.Empty;

    /// <summary>The Nutrition stat of the meat race's butchery yields, for any pawn def (the animal race table holds animals only); null for a race with no meat or a meat that shows no nutrition.</summary>
    public double? RaceMeatNutrition(string race)
    {
        if (FindThing(race) is not { race: not null } row || RaceMeatDef(row) is not { Length: > 0 } meat)
        {
            return null;
        }

        var (n, shown) = ShownStatValue(meat, string.Empty, StatNames.Nutrition);
        return shown && n > 0 ? n : null;
    }

    // RaceTicks is a LifeStageAge's minAge in ticks: Mathf.FloorToInt(minAge * GenDate.TicksPerYear) in the game's
    // single-precision arithmetic.
    private static long RaceTicks(float minAge) => (long)Math.Floor((double)(minAge * GameTime.TicksPerYear));

    // RaceStageAges: Adult is Pawn_AgeTracker.AdultMinAge (a humanlike race's first adult stage, otherwise the last
    // stage), the others the minAge of the first stage whose LifeStageDef is reproductive, milkable or shearable.
    private static RaceStageAges RaceStageAgesOf(RaceProperties props)
    {
        var humanlike = props.intelligence >= Intelligence.Humanlike;
        float adult = 0, last = 0;
        var adultFound = false;
        long? reproductive = null, milkable = null, shearable = null;
        foreach (var age in props.lifeStageAges.Seq())
        {
            var ticks = RaceTicks(age.minAge);
            if (reproductive is null && age.def.reproductive)
            {
                reproductive = ticks;
            }

            if (milkable is null && age.def.milkable)
            {
                milkable = ticks;
            }

            if (shearable is null && age.def.shearable)
            {
                shearable = ticks;
            }

            if (!adultFound && (age.def.developmentalStage & DevelopmentalStage.Adult) != 0)
            {
                adult = age.minAge;
                adultFound = true;
            }

            last = age.minAge;
        }

        var adultTicks = humanlike && adultFound ? RaceTicks(adult) : !humanlike ? RaceTicks(last) : 0;
        return new RaceStageAges(adultTicks, reproductive, milkable, shearable);
    }

    // RaceTrainables are the TrainableDefs the race can ever learn (Pawn_TrainingTracker.CanAssignToTrain's race rules:
    // trainability rank, minimum body size and the tag lists), sorted.
    private List<string> RaceTrainables(RaceProperties props)
    {
        if (props.trainability is not { } rank)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var trainable in Rows<TrainableDef>().Values.OrderBy(t => t.defName, StringComparer.Ordinal))
        {
            if (trainable.requiredTrainability is not { } required || rank.intelligenceOrder < required.intelligenceOrder || props.baseBodySize < trainable.minBodySize)
            {
                continue;
            }

            bool Matches(string tag) => tag == trainable.defName || trainable.tags.Seq().Contains(tag);
            if (props.untrainableTags.Seq().Any(Matches))
            {
                continue;
            }

            if (!props.trainableTags.Seq().Any() || props.trainableTags.Any(Matches))
            {
                result.Add(trainable.defName);
            }
        }

        return result;
    }

    // The mech kinds: the name of the one mech kind whose race is race, empty when there is none (several kinds of one
    // race are a contract failure). A mech kind is a PawnKindDef whose race is a mechanoid with an overseer-subject comp.
    private string MechKindOfRace(string race)
    {
        var kinds = Rows<PawnKindDef>().Values
            .Where(kind => kind.race is { } r && r.defName == race && RaceFlags(race).Mechanoid && HasComp<CompProperties_OverseerSubject>(r))
            .Select(kind => kind.defName)
            .Order(StringComparer.Ordinal)
            .ToList();
        return kinds.Count switch
        {
            0 => string.Empty,
            1 => kinds[0],
            _ => throw new CatalogException($"recipe produces race {race}, which has several mech kinds [{string.Join(' ', kinds)}]"),
        };
    }
}
