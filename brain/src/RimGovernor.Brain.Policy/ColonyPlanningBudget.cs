namespace RimGovernor.Brain.Policy;

public static partial class ColonyPlanningRules
{
    /// <summary>Applies the reviewed reserve target to the field coverage calculation.</summary>
    public static ReviewFacts ApplyFieldBudget(ReviewFacts facts, double reserveDays) =>
        facts with { FieldCoverage = FieldCapacityRules.FieldCoverage(facts.Census.ColonistCount, facts.FieldCrops, reserveDays) };
}