using RimWorld;
using Verse;
using Domain = RimGovernor.Brain.Domain;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The room stats the planners aim at and the price of a butchering to a room.

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

    private static IReadOnlyList<P.CurvePoint> Curve(RoomStatDef? stat) => [.. stat?.curve?.Points.Select(point => new P.CurvePoint(point.x, point.y)) ?? []];

    // The room stat a bed's SurgerySuccessChanceFactor multiplies (#3804): no RoomStatDefOf field names it; the stat's
    // StatPart_RoomStat holds it, the typed route.
    private RoomStatDef? SurgeryRoomStat() => DefRow<StatDef>(nameof(StatDefOf.SurgerySuccessChanceFactor))?.parts.Seq()
        .OfType<StatPart_RoomStat>().Select(part => GameMembers.RoomStat(part)).FirstOrDefault(stat => stat is not null);

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
            Cleanliness = blood.statBases.Seq().LastOrDefault(modifier => modifier.stat?.defName == StatNames.Cleanliness)?.value ?? 0,
            FoodPoison = [.. stat.curve?.Points.Select(point => new P.CurvePoint(point.x, point.y)) ?? []],
            FoodPoisonRoomless = stat.roomlessScore,
            InfectionChance = Curve(DefRow<RoomStatDef>(nameof(RoomStatDefOf.InfectionChanceFactor))),
            InfectionChanceRoomless = DefRow<RoomStatDef>(nameof(RoomStatDefOf.InfectionChanceFactor))?.roomlessScore ?? 0,
            SurgeryCleanliness = Curve(SurgeryRoomStat()),
            SurgeryCleanlinessRoomless = SurgeryRoomStat()?.roomlessScore ?? 0,
            StandTicks = GameConstants.MinTicksSinceThickened,
        };
    }
}
