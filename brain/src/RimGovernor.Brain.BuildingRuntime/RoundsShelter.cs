using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>The layout module a facility ladder furnishes, named by its room role; none without a facility.</summary>
    internal (Pol.PlannedRole Role, bool Ok) RoomModule() => Facility is not null ? InteriorRules.PlannedRoleOf(Facility.Role) : (default, false);

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
        return Phase == Pol.Phase.HousingSleeping || Phase == Pol.Phase.ComfortRanked;
    }
}

partial class RoundsBuildingPlanner
{
    /// <summary>Whether any colonist can raise every named definition now (no construction skill needed): the zero Verdict when so, else the first thing missing, named.</summary>
    internal static Verdict DefinitionsGate(RimGovernor.Brain.Bridge.Contracts.ColonyReading facts, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var (def, found) = DefinitionRow(facts, name);
            if (!found)
            {
                return FieldUnavailable(name + "_definition");
            }
            var availability = DefinitionAvailability(def);
            if (!availability.IsZero)
            {
                return availability;
            }
            var skill = def.ConstructionSkill;
            if (skill != 0)
            {
                return Refuse(Pol.Cause.NoWorker, "builder_for_" + name, string.Create(CultureInfo.InvariantCulture, $"construction_skill_{skill}"));
            }
        }
        return new Verdict();
    }

    internal static (RimGovernor.Brain.Policy.PlanningDefinition Def, bool Found) DefinitionRow(RimGovernor.Brain.Bridge.Contracts.ColonyReading facts, string name)
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
        var ready = def.Available;
        if (ready)
        {
            return new Verdict();
        }
        if (def.Research.Count == 0)
        {
            return AwaitingPlan(def.Name, "unavailable");
        }
        return ResearchWait(string.Join("+", def.Research));
    }


    /// <summary>
    /// Folds one more preview's stock into a bundle's. Previews of one bundle are native calls made one after
    /// another, and an Available count can differ between them (it is net of the native blueprint census and
    /// moves as colonists haul); the bundle is funded from the lowest value any preview saw, and an unknown one
    /// leaves the resource unknown. Returns the merged observation.
    /// </summary>
    internal static Pol.StockObservation MergeRoundsStock(Pol.StockObservation stock, Pol.StockObservation next)
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
                throw new ControlException("mergeRoundsStock: seen[v.Resource]");
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
