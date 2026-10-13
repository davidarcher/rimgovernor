using RimGovernor.Brain.Policy;
using RimGovernor.Protocol.Observations;

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
        var life = OptionalFact(health.HasLifeThreatening, health.LifeThreatening);
        var completeness = health.HediffCompleteness;
        if (completeness is null || completeness.Filtered != 0 || !health.HasHiddenHediffs || health.HiddenHediffs != 0 || IssueNames(health.Issues, "hediffs"))
        {
            return (unknown, life);
        }
        var rows = new List<CareCondition>(health.Hediffs.Count);
        foreach (var condition in health.Hediffs)
        {
            rows.Add(
                new CareCondition
                {
                    Severity = OptionalFact(condition.HasSeverity, condition.Severity),
                    SeverityPerDay = OptionalFact(condition.HasSeverityPerDay, condition.SeverityPerDay),
                    Immunity = OptionalFact(condition.HasImmunity, condition.Immunity),
                    ImmunityPerDay = OptionalFact(condition.HasImmunityPerDay, condition.ImmunityPerDay),
                    Tended = OptionalFact(condition.HasTended, condition.Tended),
                    TendQuality = OptionalFact(condition.HasTendQuality, condition.TendQuality),
                    PartIndex = OptionalInt(condition.HasPartIndex, condition.PartIndex),
                    DefName = OptionalFact(condition.HasDefName, condition.DefName),
                }
            );
        }
        return (Known(EquatableArray.From(rows)), life);
    }
}
