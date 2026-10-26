using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

public sealed partial class BuildingPlanner
{
    /// <summary>
    /// Whether the planner walks a facility ladder whose furniture stands in a planned room (comfort, workshop,
    /// hospital, sleeping, laboratory).
    /// </summary>
    internal bool FacilityLadder()
    {
        if (Concern == EconomyFamily.Resource.FilingKey || Concern == MilitaryFamily.Equipment.FilingKey || Concern == MedicalRules.MedicalCareNeed.FilingKey || Concern == EconomyFamily.Research.FilingKey)
        {
            return true;
        }
        return Phase == Pol.Phase.HousingSleeping || Phase == Pol.Phase.ComfortRanked || Phase == Pol.Phase.ComfortBasic;
    }
}

partial class BuildingPlanner
{
    /// <summary>Whether any colonist can raise every named definition now (no construction skill needed): the zero Verdict when so, else the first thing missing, named.</summary>
    internal static Verdict DefinitionsGate(RimGovernor.Brain.GameAccess.ColonyReading facts, IReadOnlyList<string> names)
    {
        var gate = FacilityPlanningRules.DefinitionsGate(facts.Facts.Definitions, names);
        return gate.Cause == Cause.None ? default : Refuse(gate.Cause, gate.Subject, gate.Detail);
    }

    internal static (RimGovernor.Brain.Policy.PlanningDefinition Def, bool Found) DefinitionRow(RimGovernor.Brain.GameAccess.ColonyReading facts, string name)
    {
        foreach (var def in facts.Facts.Definitions)
        {
            if (def.Name == name)
            {
                return (def, true);
            }
        }
        return (new RimGovernor.Brain.Policy.PlanningDefinition(), false);
    }

    /// <summary>The zero Verdict for a definition the game reports buildable, else the missing fact or the research it waits on.</summary>
    internal static Verdict DefinitionAvailability(RimGovernor.Brain.Policy.PlanningDefinition def)
    {
        var gate = FacilityPlanningRules.DefinitionAvailability(def);
        return gate.Cause == Cause.None ? default : Refuse(gate.Cause, gate.Subject, gate.Detail);
    }


    /// <summary>
    /// Folds one more preview's stock into a bundle's. Previews of one bundle are native calls made one after
    /// another, and an Available count can differ between them (it is net of the native blueprint census and
    /// moves as colonists haul); the bundle is funded from the lowest value any preview saw, and an unknown one
    /// leaves the resource unknown. Returns the merged observation.
    /// </summary>
    internal static Pol.StockObservation MergeReviewStock(Pol.StockObservation stock, Pol.StockObservation next)
    {
        var values = stock.Values.ToList();
        var index = new Dictionary<Pol.Resource, int>();
        for (var i = 0; i < values.Count; i++)
        {
            index[values[i].Resource] = i;
        }
        var seen = new HashSet<Pol.Resource>();
        foreach (var v in next.Values)
        {
            if (!seen.Add(v.Resource))
            {
                throw new ControlException("mergeReviewStock: seen[v.Resource]");
            }
            if (index.TryGetValue(v.Resource, out var at))
            {
                values[at] = values[at] with { Available = Math.Min(values[at].Available, v.Available) };
            }
            else
            {
                values.Add(v);
                index[v.Resource] = values.Count - 1;
            }
        }
        return stock with { Values = [.. values] };
    }

}
