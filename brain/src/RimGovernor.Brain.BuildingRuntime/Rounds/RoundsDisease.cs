namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>The work demand of the review: the policy demand, the construction help owed and the disease rest of the medical pawns.</summary>
    internal static WorkDemand RoundsDiseaseDemand(ColonyProjection facts, IReadOnlyList<string> definitions, RoundsReview previous, GenerationSnapshot current)
    {
        var demand = PolicyApi.RoundsWorkDemand(facts.Facts, definitions.Count > 0);
        var history = previous.MedicalCare.Resting;
        var helped = previous.Roster?.Help;
        if (previous.Snapshot.Colony != current.Colony || previous.Snapshot.Load != current.Load || previous.Snapshot.Map != current.Map || facts.Identity.Tick < previous.Tick)
        {
            history = [];
            helped = null;
        }
        demand = demand with { Help = PolicyApi.ConstructionHelpDemand(previous.ReadyWork, current, facts.Identity.Tick, HelpDefinitions(definitions, facts.Definitions), helped, facts.Facts.CurrentConstruction) };
        demand = demand with { Resting = PolicyApi.ReviewDiseaseRest(facts.Facts.MedicalPawns, history) };
        return demand;
    }

    internal static List<HelpDefinition> HelpDefinitions(IReadOnlyList<string> names, IReadOnlyList<PlanningDefinition> catalog)
    {
        var skills = new Dictionary<string, Fact<int>>(StringComparer.Ordinal);
        foreach (var d in catalog)
        {
            skills[d.Name] = d.ConstructionSkill;
        }
        var output = new List<HelpDefinition>(names.Count);
        foreach (var name in names)
        {
            var skill = Unknown<int>();
            if (skills.TryGetValue(name, out var s) && s.TryGetValue(out var v))
            {
                skill = Known(v);
            }
            output.Add(new HelpDefinition { Name = name, Skill = skill });
        }
        return output;
    }
}
