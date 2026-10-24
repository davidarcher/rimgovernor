using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

public static class CareConditionsRules
{
    /// <summary>
    /// Decodes a pawn's conditions and life threat, the inputs of the care planners.
    /// An absent conditions block leaves both unknown.
    /// </summary>
    public static (Fact<EquatableArray<CareCondition>> Conditions, Fact<bool> LifeThreatening) CareConditions(PawnConditions? conditions) =>
        conditions is null
            ? (Unknown<EquatableArray<CareCondition>>(), Unknown<bool>())
            : CareConditions(conditions.LifeThreatening, conditions.Hediffs);

    /// <summary>The care inputs of a health row: every hediff, none filtered.</summary>
    public static (Fact<EquatableArray<CareCondition>> Conditions, Fact<bool> LifeThreatening) CareConditions(PawnHealth health) =>
        CareConditions(health.LifeThreatening, health.Hediffs);

    private static (Fact<EquatableArray<CareCondition>> Conditions, Fact<bool> LifeThreatening) CareConditions(bool lifeThreatening, EquatableArray<Hediff> hediffs)
    {
        var rows = new List<CareCondition>(hediffs.Count);
        foreach (var condition in hediffs)
        {
            // CareCondition is the care planners' Fact-typed copy (#3788): a fact the hediff does not have (no
            // immunity, no part, no immunity record) stays unknown there.
            var immune = condition.Immunizable;
            var gain = immune?.Gain as ImmunityGain.PerDay;
            rows.Add(
                new CareCondition
                {
                    Severity = Known(condition.Severity),
                    SeverityPerDay = FactHelpers.OptionalFact(immune is not null, immune?.SeverityPerDay ?? 0),
                    Immunity = FactHelpers.OptionalFact(immune is not null, immune?.Immunity ?? 0),
                    ImmunityPerDay = FactHelpers.OptionalFact(gain is not null, gain?.Value ?? 0),
                    Tended = Known(condition.Tend is HediffTend.Tended),
                    PartIndex = FactHelpers.OptionalInt(condition.Part is not null, condition.Part?.Index ?? 0),
                    DefName = Known(condition.DefName),
                }
            );
        }
        return (Known(EquatableArray.From(rows)), Known(lifeThreatening));
    }
}
