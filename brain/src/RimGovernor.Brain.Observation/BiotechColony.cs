namespace RimGovernor.Brain.Observation;

/// <summary>
/// Lifts the Biotech colony section into the policy inputs that read it: the recharge policy's chargers, the
/// gestation review's gestators and wastepacks, the gene-bank Need's banks and packs and the pollution Need's
/// wastepacks and uncovered cells. A fact built from the section is unknown when Biotech is inactive.
/// </summary>
public static class BiotechColonyReads
{
    /// <summary>The section's chargers as the recharge policy reads them: charging is a mech attached.</summary>
    public static IReadOnlyList<MechCharger> MechChargerRows(O.BiotechColonyFacts biotech) =>
        [.. biotech.Chargers.Select(c => new MechCharger { Powered = c.Powered, FullOfWaste = c.FullOfWaste, Charging = c.ChargingMechId is not null })];

    /// <summary>The section's gestators as the gestation review reads them: active while a bill runs.</summary>
    public static IReadOnlyList<GestatorFact> GestatorFacts(O.BiotechColonyFacts biotech) =>
        [.. biotech.Gestators.Select(g => new GestatorFact { Active = g.BillId is not null, WasteCount = g.WasteCount })];

    /// <summary>The section's wastepack stacks as the gestation review reads them.</summary>
    public static IReadOnlyList<WastepackFact> WastepackFacts(O.BiotechColonyFacts biotech) =>
        [.. biotech.Wastepacks.Select(w => new WastepackFact { Count = w.Count, Frozen = w.Frozen, InAtomizer = w.InAtomizer })];

    /// <summary>The section's banks and packs as the biotech/gene-bank Need reads them: a pack is loose when no bank holds it.</summary>
    public static GeneBankFacts GeneBankFacts(O.BiotechColonyFacts biotech) => new()
    {
        Banks = EquatableArray.From(biotech.GeneBanks.Select(b => new GeneBankSlot { Capacity = b.Capacity, Held = b.PackIds.Count })),
        Packs = EquatableArray.From(biotech.Genepacks.Select(p => p.BankId is null)),
    };

    /// <summary>The section's chargers as a fact, unknown without Biotech.</summary>
    internal static Fact<EquatableArray<MechCharger>> MechChargerFact(O.BiotechSection section) =>
        section is O.BiotechSection.Active { Facts: var b } ? Known(EquatableArray.From(MechChargerRows(b))) : Unknown<EquatableArray<MechCharger>>();

    /// <summary>The section's gene-building rows as a fact, unknown without Biotech.</summary>
    internal static Fact<GeneBankFacts> GeneBankFact(O.BiotechSection section) =>
        section is O.BiotechSection.Active { Facts: var b } ? Known(GeneBankFacts(b)) : Unknown<GeneBankFacts>();

    /// <summary>
    /// The biotech/pollution Need's read: each wastepack with the game's verdicts and the count of polluted cells
    /// outside the pollution-clear area. Unknown without Biotech, so the Need is unknown on a colony without it.
    /// </summary>
    internal static Fact<PollutionFacts> Pollution(O.BiotechSection section)
    {
        if (section is not O.BiotechSection.Active { Facts: var b })
        {
            return Unknown<PollutionFacts>();
        }
        var wastepacks = b.Wastepacks.Select(w => new Policy.Wastepack
        {
            Id = w.ThingId,
            Definition = w.DefName,
            Cell = w.Position,
            Frozen = w.Frozen,
            InAtomizer = w.InAtomizer,
            Forbidden = w.Forbidden,
        });
        return Known(new PollutionFacts { UncoveredCells = b.PollutedUncoveredCells, Wastepacks = [.. wastepacks] });
    }

    /// <summary>The catalog definitions that are mech chargers (PlanningDefinition.MechCharger, from the game's Building_MechCharger class), in catalog order; the build planner picks among them.</summary>
    public static IReadOnlyList<PlanningDefinition> MechChargerDefs(IEnumerable<PlanningDefinition> defs)
    {
        var output = new List<PlanningDefinition>();
        foreach (var d in defs)
        {
            if (d.MechCharger.TryGetValue(out var charger) && charger)
            {
                output.Add(d);
            }
        }
        return output;
    }
}
