using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go care_conditions.go.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>CareConditions</c>: decodes a pawn's conditions and life threat, the inputs of the care planners.
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
            rows.Add(
                new CareCondition
                {
                    Severity = Known(condition.Severity),
                    SeverityPerDay = OptionalFact(condition.SeverityPerDay is not null, condition.SeverityPerDay.GetValueOrDefault()),
                    Immunity = OptionalFact(condition.Immunity is not null, condition.Immunity.GetValueOrDefault()),
                    ImmunityPerDay = OptionalFact(condition.ImmunityPerDay is not null, condition.ImmunityPerDay.GetValueOrDefault()),
                    Tended = Known(condition.Tended),
                    PartIndex = OptionalInt(condition.PartIndex is not null, condition.PartIndex.GetValueOrDefault()),
                    DefName = Known(condition.DefName),
                }
            );
        }
        return (Known(EquatableArray.From(rows)), Known(lifeThreatening));
    }
}
