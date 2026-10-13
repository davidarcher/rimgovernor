using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go care_conditions.go.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>CareConditions</c>: decodes a health row's conditions and life threat, the inputs of the care
    /// planners. A visible-only or incomplete hediff list leaves the conditions unknown: it cannot prove a
    /// condition absent.
    /// </summary>
    public static (Fact<EquatableArray<CareCondition>> Conditions, Fact<bool> LifeThreatening) CareConditions(PawnHealth? health)
    {
        var unknown = Unknown<EquatableArray<CareCondition>>();
        if (health is null)
        {
            return (unknown, Unknown<bool>());
        }
        var life = OptionalFact(health.LifeThreatening is not null, health.LifeThreatening.GetValueOrDefault());
        var completeness = health.HediffCompleteness;
        if (completeness is null || completeness.Filtered.GetValueOrDefault() != 0 || health.HiddenHediffs is null || health.HiddenHediffs.GetValueOrDefault() != 0 || IssueNames(health.Issues, "hediffs"))
        {
            return (unknown, life);
        }
        var rows = new List<CareCondition>(health.Hediffs.Count);
        foreach (var condition in health.Hediffs)
        {
            rows.Add(
                new CareCondition
                {
                    Severity = OptionalFact(condition.Severity is not null, condition.Severity.GetValueOrDefault()),
                    SeverityPerDay = OptionalFact(condition.SeverityPerDay is not null, condition.SeverityPerDay.GetValueOrDefault()),
                    Immunity = OptionalFact(condition.Immunity is not null, condition.Immunity.GetValueOrDefault()),
                    ImmunityPerDay = OptionalFact(condition.ImmunityPerDay is not null, condition.ImmunityPerDay.GetValueOrDefault()),
                    Tended = OptionalFact(condition.Tended is not null, condition.Tended.GetValueOrDefault()),
                    TendQuality = OptionalFact(condition.TendQuality is not null, condition.TendQuality.GetValueOrDefault()),
                    PartIndex = OptionalInt(condition.PartIndex is not null, condition.PartIndex.GetValueOrDefault()),
                    DefName = OptionalFact(condition.DefName is not null, (condition.DefName ?? "")),
                }
            );
        }
        return (Known(EquatableArray.From(rows)), life);
    }
}
