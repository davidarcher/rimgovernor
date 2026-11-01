namespace RimGovernor.Brain.Policy;

/// <summary>
/// The barn alternatives (RG-HABITAT-001): whether a pen unit rests in a barn beside its pen, compared as a layout regroup
/// deviation by the shared canonical ledger rather than picked by the shelter's own gain. The baseline plans no new barn
/// (a standing barn stays a node, LayoutPlan.HerdRoomNodes); each deviation adds one barn for a unit whose barn
/// alternative (HerdShelterRules.Assess) avoids a lethal exposure, sized HerdShelterRules.BarnCellsPerAnimal per penned
/// animal. A unit whose barn avoids nothing (a hardy species, a comfort range covering the climate), is excluded or is
/// unassessed is offered none. The forecast prices the planned barn (ForecastHerdShelter) against what it costs to build;
/// the baseline keeps ties. The herd arrangement's shelter deficit closes only on the observed shelter its pen reaches.
/// </summary>
public static class LayoutHerdBarnRules
{
    /// <summary>The regroup decision kind of a barn alternative (A: <see cref="Barns"/>, B: the unit and its cells).</summary>
    public const string BarnMove = "herd_barn";

    /// <summary>The decision's A: the units' barns.</summary>
    public const string Barns = "barns";

    /// <summary>The alternative's id, which also separates its trials' signatures from the baseline's.</summary>
    public static string IdOf(BarnWant w) => "barn:" + NameOf(w);

    /// <summary>The decision's B: the unit ("misc" for the misc unit) and the barn's cells.</summary>
    public static string NameOf(BarnWant w) => (w.Race.Length == 0 ? "misc" : w.Race) + "x" + w.Cells.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The barns compared against the baseline: one per unit (in unit key order) whose barn alternative is assessed with a
    /// gain above zero and whose standing barns fall short of the cells its penned animals sleep on.
    /// </summary>
    public static IReadOnlyList<BarnWant> DeviationsOf(StationFacts f)
    {
        var output = new List<BarnWant>();
        foreach (var shelter in f.HerdShelters.OrderBy(s => s.Herd, StringComparer.Ordinal))
        {
            if (!shelter.Alternatives.Any(a => a.Kind == HerdShelterKind.Barn && a.Value is HerdShelterValue.Assessed { GainPerDay: > 0 }))
            {
                continue;
            }
            var cells = shelter.Animals * HerdShelterRules.BarnCellsPerAnimal;
            var standing = f.Plan.HerdRooms(PlannedRole.Barn, shelter.Herd).Sum(r => r.Region.Bounds().Width * r.Region.Bounds().Height);
            if (cells > standing)
            {
                output.Add(new BarnWant(shelter.Herd, cells));
            }
        }
        return output;
    }

    /// <summary>The input per barn deviation (on the baseline hosts), each adding its unit's barn to the facts' barns.</summary>
    public static IReadOnlyList<HostedRegroupInput> AlternativesOf(StationFacts f, HostedRegroupInput baseline) =>
        [.. DeviationsOf(f).Select(w => new HostedRegroupInput(baseline.Host with { Id = IdOf(w) },
            LayoutStationPlanRules.RegroupInputOf(f with { Barns = [.. f.Barns, w] }, baseline.Host)) { Barn = w })];
}
