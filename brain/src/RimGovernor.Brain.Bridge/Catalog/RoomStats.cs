using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// Go catalog_room_stats.go: the room stats the planners aim at and the price of a butchering to a room.

public sealed partial class DefinitionCatalog
{
    /// <summary>The RoomStatDef the room Impressiveness stat reads and whose score stages name the impressiveness labels.</summary>
    public const string RoomStatImpressiveness = "Impressiveness";

    // The RoomStatDef a cook's meals read; its input is the room's Cleanliness.
    private const string RoomStatFoodPoisonChance = "FoodPoisonChance";

    // The positions of the stages the planners aim at in the Impressiveness stat's score stages, which run awful, dull,
    // mediocre, decent, slightly impressive and on up.
    private const int ImpressivenessDull = 1;
    private const int ImpressivenessMediocre = 2;
    private const int ImpressivenessDecent = 3;
    private const int ImpressivenessSlightlyImpressive = 4;

    /// <summary>
    /// The minimum scores of the stages the planners aim at, read from the Impressiveness RoomStatDef's score stages. A
    /// catalog without the def, with too few stages, or whose stage scores do not rise from a positive dull stage is an error.
    /// </summary>
    public P.ImpressivenessLevels ImpressivenessLevels()
    {
        var row = DefRow<RoomStatDef>(RoomStatImpressiveness) ?? throw new CatalogException($"catalog has no {RoomStatImpressiveness} room stat");
        var stages = row.scoreStages.Seq().Select(stage => (double)stage.minScore).ToList();
        if (stages.Count <= ImpressivenessSlightlyImpressive)
        {
            throw new CatalogException($"room stat {RoomStatImpressiveness} has {stages.Count} score stages, want more than {ImpressivenessSlightlyImpressive}");
        }

        if (stages.Any(v => !double.IsFinite(v)))
        {
            throw new CatalogException($"room stat {RoomStatImpressiveness} has a non-finite score stage");
        }

        var levels = new P.ImpressivenessLevels
        {
            Dull = stages[ImpressivenessDull],
            Mediocre = stages[ImpressivenessMediocre],
            Decent = stages[ImpressivenessDecent],
            SlightlyImpressive = stages[ImpressivenessSlightlyImpressive],
            Stages = new([.. stages]),
        };
        return levels.Dull > 0 && levels.Dull < levels.Mediocre && levels.Mediocre < levels.Decent && levels.Decent < levels.SlightlyImpressive
            ? levels
            : throw new CatalogException($"room stat {RoomStatImpressiveness} stage scores {levels} do not rise from a positive first stage");
    }

    // The BloodDef most humanlike races name, the filth Corpse.ButcherProducts spawns once per butchered colonist-sized
    // corpse (ties by name); empty when no humanlike race names one.
    private string HumanlikeBloodDef() =>
        _thingDefs.Values
            .Where(row => row.race is { intelligence: >= Intelligence.Humanlike } race && GameMembers.BloodDef(race) is not null)
            .GroupBy(row => GameMembers.BloodDef(row.race)!.defName)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault() ?? string.Empty;

    // The FoodPoisoning hediff as the price reads it: the severity it starts at and loses a day (its SeverityPerDay comp)
    // and each stage's factor on the work capacity and vomit interval. A catalog without the def, or whose hediff does not
    // fade, prices no poisoning (the zero model).
    private P.PoisoningModel PoisoningModel()
    {
        var table = P.PolicyFunctions.FoodPoisonGame();
        if (DefRow<HediffDef>(table.Hediff) is not { } hediff)
        {
            return new P.PoisoningModel();
        }

        var perDay = hediff.comps.Seq().OfType<HediffCompProperties_SeverityPerDay>().Where(comp => comp.severityPerDay < 0).Select(comp => -(double)comp.severityPerDay).LastOrDefault();
        var stages = hediff.stages.Seq().Select(stage =>
        {
            var factor = stage.capMods.Seq().Where(mod => mod.capacity?.defName == table.WorkCapacity).Aggregate(1.0, (product, mod) => product * mod.postFactor);
            return new P.PoisonStage(stage.minSeverity, factor, stage.vomitMtbDays);
        });
        return new P.PoisoningModel { Initial = hediff.initialSeverity, PerDay = perDay, Stages = [.. stages] };
    }

    /// <summary>The price of a butchering to a room, from the defs: the humanlike blood filth's Cleanliness stat and the FoodPoisonChance curve. A catalog without either prices nothing (the zero model).</summary>
    public P.FilthModel FilthModel()
    {
        var name = HumanlikeBloodDef();
        if (FindThing(name) is not { } blood || DefRow<RoomStatDef>(RoomStatFoodPoisonChance) is not { } stat)
        {
            return new P.FilthModel();
        }

        return new P.FilthModel
        {
            Def = name,
            Poisoning = PoisoningModel(),
            Cleanliness = blood.statBases.Seq().LastOrDefault(modifier => modifier.stat?.defName == StatNames.Cleanliness)?.value ?? 0,
            FoodPoison = [.. stat.curve?.Points.Select(point => new P.CurvePoint(point.x, point.y)) ?? []],
            CleaningWork = blood.filth?.cleaningWorkToReduceThickness ?? 0,
            CleaningSpeed = DefRow<StatDef>(P.PolicyFunctions.FilthGameTable().SpeedStat)?.defaultBaseValue ?? 0,
            StandTicks = GameConstants.MinTicksSinceThickened,
        };
    }
}
