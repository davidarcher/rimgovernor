namespace RimGovernor.Brain.Observation;

/// <summary>
/// Lifts the Biotech colony section into the gestation review's inputs: its gestators and wastepacks. The chargers,
/// gene buildings and pollution census are read off the section by the Policy rules (MechRules.MechChargersOf,
/// GeneBankConcernRules.GeneBankOwed, PollutionRules.PollutionOf).
/// </summary>
public static class BiotechColonyReads
{
    /// <summary>The section's gestators as the gestation review reads them: active while a bill runs.</summary>
    public static IReadOnlyList<GestatorFact> GestatorFacts(O.BiotechColonyFacts biotech) =>
        [.. biotech.Gestators.Select(g => new GestatorFact { Active = g.BillId is not null, WasteCount = g.WasteCount })];

    /// <summary>The section's wastepack stacks as the gestation review reads them.</summary>
    public static IReadOnlyList<WastepackFact> WastepackFacts(O.BiotechColonyFacts biotech) =>
        [.. biotech.Wastepacks.Select(w => new WastepackFact { Count = w.Count, Frozen = w.Frozen, InAtomizer = w.InAtomizer })];

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
