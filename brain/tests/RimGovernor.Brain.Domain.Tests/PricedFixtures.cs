using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Gives a generated coupled review what production observes and the capability-days comparison prices (#3522): the capacity
/// penalty tables, every pawn's capacity levels and hediff severities, and a mood-break model. Values a fixture already set
/// are kept, so a case that withholds one still shows its term unassessed. Fixture horizons are a fraction of a day, so the
/// policy's tie resolution (capability-days over a one-day horizon) is scaled to the fixture's horizon.
/// </summary>
internal static class PricedFixtures
{
    public const double DefaultHorizon = 300;

    public static RoundsFacts Priced(this RoundsFacts facts, double horizon = DefaultHorizon) =>
        facts.CoupledAlternatives is null ? facts : facts with { CoupledAlternatives = facts.CoupledAlternatives.Priced(horizon) };

    public static ForecastCoupledCandidates Priced(this ForecastCoupledCandidates candidates, double horizon = DefaultHorizon)
    {
        var context = candidates.Context;
        return candidates with { Context = context with
        {
            Needs = context.Needs with { Pawns = EquatableArray.From(context.Needs.Pawns.Select(Equipped)) },
            CapacityPenalties = context.CapacityPenalties.Malnutrition.Valid() ? context.CapacityPenalties : Tables(),
            MoodBreaks = context.MoodBreaks.Tiers.Count != 0 ? context.MoodBreaks : new() { Threshold = 0.3, Tiers = [new(1, 2), new(1, 3), new(1, 5)] },
            Value = context.Value with { TieResolution = context.Value.TieResolution * horizon / GameTime.TicksPerDay },
        } };
    }

    public static NeedsForecastPawn Equipped(NeedsForecastPawn pawn) => pawn with
    {
        Consciousness = pawn.Consciousness.IsKnown ? pawn.Consciousness : Known(1d), Moving = pawn.Moving.IsKnown ? pawn.Moving : Known(1d),
        Manipulation = pawn.Manipulation.IsKnown ? pawn.Manipulation : Known(1d), Sight = pawn.Sight.IsKnown ? pawn.Sight : Known(1d),
        Talking = pawn.Talking.IsKnown ? pawn.Talking : Known(1d),
        MalnutritionSeverity = pawn.MalnutritionSeverity.IsKnown ? pawn.MalnutritionSeverity : Known(0d),
        FoodPoisoningSeverity = pawn.FoodPoisoningSeverity.IsKnown ? pawn.FoodPoisoningSeverity : Known(0d),
        HypothermiaSeverity = pawn.HypothermiaSeverity.IsKnown ? pawn.HypothermiaSeverity : Known(0d),
        HeatstrokeSeverity = pawn.HeatstrokeSeverity.IsKnown ? pawn.HeatstrokeSeverity : Known(0d),
    };

    // Any starving or poisoned or frozen stretch halves consciousness: the tests need a price, not the game's stage table.
    public static CapacityPenaltyTables Tables() => new()
    {
        Malnutrition = Table("Malnutrition", null), FoodPoisoning = Table("FoodPoisoning", -1), Hypothermia = Table("Hypothermia", null), Heatstroke = Table("Heatstroke", null),
        MovingMinForCapable = 0.15,
    };

    private static HediffPenaltyTable Table(string name, double? perDay) => new()
    {
        Known = true, Hediff = name, LethalSeverity = 1, InitialSeverity = name == "FoodPoisoning" ? 1 : 0.001, SeverityPerDay = perDay,
        Stages = [new PenaltyStage(0, 0, [new CapacityModifier("Consciousness", 0, null, 0.5)])],
    };
}
