using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The capacity penalty tables of #3519, read by the capability-days ledger (#3520, #3521): malnutrition, food poisoning, hypothermia and heatstroke, each
// decoded from its HediffDef (stages by minSeverity, the capacity modifiers, pain, the lethal severity and the severity
// comps), and the Moving capacity's minForCapable. A hediff the catalog lacks is an unknown table, never an empty one.
//
// A modifier counts when its capacity is one of the five a pawn-day of capacity is counted in (consciousness, moving,
// manipulation, sight, talking). Food poisoning also names the Eating capacity, which is no part of a pawn-day, so it is
// left out. The defs' floats widen to the decimal their XML wrote (Decimal32).

public sealed partial class DefinitionCatalog
{
    private static readonly HashSet<string> PawnDayCapacities = new(StringComparer.Ordinal)
    {
        nameof(PawnCapacityDefOf.Consciousness), nameof(PawnCapacityDefOf.Moving), nameof(PawnCapacityDefOf.Manipulation),
        nameof(PawnCapacityDefOf.Sight), nameof(PawnCapacityDefOf.Talking),
    };

    /// <summary>The capacity penalty tables; a table is unknown when the catalog has no row for its def.</summary>
    public P.CapacityPenaltyTables CapacityPenalties() => _capacityPenalties.Value;

    private P.CapacityPenaltyTables BuildCapacityPenalties() => new()
    {
        Malnutrition = PenaltyTable(nameof(HediffDefOf.Malnutrition)),
        FoodPoisoning = PenaltyTable(nameof(HediffDefOf.FoodPoisoning)),
        Hypothermia = PenaltyTable(nameof(HediffDefOf.Hypothermia)),
        Heatstroke = PenaltyTable(nameof(HediffDefOf.Heatstroke)),
        MovingMinForCapable = DefRow<PawnCapacityDef>(nameof(PawnCapacityDefOf.Moving)) is { } moving ? Decimal32(moving.minForCapable) : null,
    };

    private P.HediffPenaltyTable PenaltyTable(string name)
    {
        if (DefRow<HediffDef>(name) is not { } hediff)
        {
            return new P.HediffPenaltyTable();
        }

        var perDay = hediff.comps.Seq().OfType<HediffCompProperties_SeverityPerDay>().FirstOrDefault();
        var gone = hediff.comps.Seq().OfType<HediffCompProperties_Disappears>().FirstOrDefault();
        return new P.HediffPenaltyTable
        {
            Known = true,
            Hediff = name,
            LethalSeverity = hediff.lethalSeverity > 0 ? Decimal32(hediff.lethalSeverity) : null,
            InitialSeverity = Decimal32(hediff.initialSeverity),
            SeverityPerDay = perDay is null ? null : Decimal32(perDay.severityPerDay),
            DisappearsAfterTicks = gone is null ? null : (gone.disappearsAfterTicks.min, gone.disappearsAfterTicks.max),
            Stages =
            [
                .. hediff.stages.Seq().OrderBy(s => s.minSeverity).Select(s => new P.PenaltyStage(
                    Decimal32(s.minSeverity),
                    Decimal32(s.painOffset),
                    [.. s.capMods.Seq().Where(m => m.capacity is not null && PawnDayCapacities.Contains(m.capacity.defName)).Select(m => new P.CapacityModifier(
                        m.capacity.defName, Decimal32(m.offset), m.SetMaxDefined ? Decimal32(m.setMax) : null, Decimal32(m.postFactor)))])),
            ],
        };
    }
}
