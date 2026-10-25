using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge.Contracts;
using D = RimGovernor.Contracts.Wire.Defs;
using O = RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Bridge;

/// <summary>Catalog definitions used to plan a mech charger.</summary>
public static class BiotechColonyReads
{
    /// <summary>The catalog definitions that are mech chargers (PlanningDefinition.MechCharger, from the game's Building_MechCharger class), in catalog order; the build planner picks among them.</summary>
    public static IReadOnlyList<PlanningDefinition> MechChargerDefs(IEnumerable<PlanningDefinition> defs)
    {
        var output = new List<PlanningDefinition>();
        foreach (var d in defs)
        {
            var charger = d.MechCharger;
            if (charger)
            {
                output.Add(d);
            }
        }
        return output;
    }
}
