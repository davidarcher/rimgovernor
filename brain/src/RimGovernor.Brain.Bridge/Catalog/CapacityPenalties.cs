using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The capacity penalty tables of #3519, read by the capability-days ledger (#3520, #3521) and the harm model (#3544): malnutrition, food poisoning,
// hypothermia, heatstroke, blood loss and wound infection, each
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

    /// <summary>The capacity penalty tables; a table is unknown when the catalog has no row for its def, and every table is when it has none for the Moving capacity, whose minForCapable prices them all.</summary>
    public P.CapacityPenaltyTables CapacityPenalties() => _capacityPenalties.Value;

    private P.CapacityPenaltyTables BuildCapacityPenalties() => DefRow<PawnCapacityDef>(nameof(PawnCapacityDefOf.Moving)) is not { } moving
        ? new()
        : new()
        {
            Malnutrition = PenaltyTable(nameof(HediffDefOf.Malnutrition)),
            FoodPoisoning = PenaltyTable(nameof(HediffDefOf.FoodPoisoning)),
            Hypothermia = PenaltyTable(nameof(HediffDefOf.Hypothermia)),
            Heatstroke = PenaltyTable(nameof(HediffDefOf.Heatstroke)),
            BloodLoss = PenaltyTable(nameof(HediffDefOf.BloodLoss)),
            WoundInfection = PenaltyTable(nameof(HediffDefOf.WoundInfection)),
            MovingMinForCapable = Decimal32(moving.minForCapable),
        };

    private P.HediffPenaltyTable PenaltyTable(string name)
    {
        if (DefRow<HediffDef>(name) is not { } hediff)
        {
            return new P.HediffPenaltyTable();
        }

        var perDay = hediff.comps.Seq().OfType<HediffCompProperties_SeverityPerDay>().FirstOrDefault();
        var gone = hediff.comps.Seq().OfType<HediffCompProperties_Disappears>().FirstOrDefault();
        var immunizable = hediff.comps.Seq().OfType<HediffCompProperties_Immunizable>().FirstOrDefault();
        var tend = hediff.comps.Seq().OfType<HediffCompProperties_TendDuration>().FirstOrDefault();
        return new P.HediffPenaltyTable
        {
            Known = true,
            Hediff = name,
            Lethality = hediff.lethalSeverity > 0 ? new P.Lethality.Lethal(Decimal32(hediff.lethalSeverity)) : new P.Lethality.Harmless(),
            InitialSeverity = Decimal32(hediff.initialSeverity),
            Drift = perDay is null ? new P.SeverityDrift.Steady() : new P.SeverityDrift.Drifting(Decimal32(perDay.severityPerDay)),
            Lifespan = gone is null ? new P.Lifespan.Lasting() : new P.Lifespan.EndsAfter(gone.disappearsAfterTicks.min, gone.disappearsAfterTicks.max),
            Immunizable = immunizable is null ? null : new P.ImmunizableProps(
                Decimal32(immunizable.immunityPerDaySick), Decimal32(immunizable.severityPerDayNotImmune), Decimal32(immunizable.severityPerDayImmune)),
            Tending = tend is null ? new P.TendEffect.Untendable() : new P.TendEffect.Tendable(Decimal32(tend.severityPerDayTended)),
            Stages =
            [
                .. hediff.stages.Seq().OrderBy(s => s.minSeverity).Select(s => new P.PenaltyStage(
                    Decimal32(s.minSeverity),
                    Decimal32(s.painOffset),
                    [.. s.capMods.Seq().Where(m => m.capacity is not null && PawnDayCapacities.Contains(m.capacity.defName)).Select(m => new P.CapacityModifier(
                        m.capacity.defName, Decimal32(m.offset), m.SetMaxDefined ? new P.CapacityCeiling.Capped(Decimal32(m.setMax)) : new P.CapacityCeiling.Uncapped(), Decimal32(m.postFactor)))]) { PartEfficiencyOffset = Decimal32(s.partEfficiencyOffset) }),
            ],
        };
    }
}
