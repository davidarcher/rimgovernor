using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

internal static class Disease
{
    /// <summary>The work demand of the review: the policy demand, the construction help owed and the disease rest of the medical pawns.</summary>
    internal static WorkDemand DiseaseDemand(ColonyReading facts, IReadOnlyList<string> definitions, ReviewStep previous, GenerationSnapshot current)
    {
        var demand = WorkRules.WorkDemand(facts.Facts, definitions.Count > 0);
        var history = previous.MedicalCare.Resting;
        var helped = previous.Roster?.Help;
        if (previous.Snapshot.Colony != current.Colony || previous.Snapshot.Load != current.Load || previous.Snapshot.Map != current.Map || facts.Identity.Tick < previous.Tick)
        {
            history = [];
            helped = null;
        }
        demand = demand with { Help = ConstructionHelpersRules.ConstructionHelpDemand(previous.ReadyWork, current, facts.Identity.Tick, helped, facts.Facts.CurrentConstruction) };
        demand = demand with { Resting = DiseaseRestRules.ReviewDiseaseRest(facts.Facts.MedicalPawns, history) };
        return demand;
    }

}
