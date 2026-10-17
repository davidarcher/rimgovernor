namespace RimGovernor.Brain.Policy;

// Drug runway keys. A social drug is spent where a colonist takes it; the realized-consumption ring counts each dose,
// and ForecastResourceRunway turns the observed rate and the stock into days left.
public static partial class PolicyFunctions
{
    /// <summary>
    /// The runway keys of the catalog's social drugs, each at one dose per colonist whose drug policy permits it
    /// (users), so a drug nobody has taken yet is still stocked; once doses are taken the observed rate takes over.
    /// </summary>
    public static Dictionary<Resource, long> DrugRunwayReserves(ItemFacts items, IReadOnlyDictionary<Resource, long> users)
    {
        var output = new Dictionary<Resource, long>();
        foreach (var drug in items.RecreationDrugs())
        {
            output[drug.Def] = Math.Max(0, users.GetValueOrDefault(drug.Def));
        }
        return output;
    }

    /// <summary>Counts, for each social drug, the colonists holding a drug policy that allows it (joy, addiction or scheduled use).</summary>
    public static Dictionary<Resource, long> DrugUsers(ItemFacts items, IEnumerable<OwnedDrugPolicy> policies)
    {
        var social = items.RecreationDrugs();
        var output = new Dictionary<Resource, long>();
        foreach (var p in policies)
        {
            foreach (var entry in p.Entries)
            {
                var drug = new Resource(entry.Drug);
                if (!entry.Off && social.Any(d => d.Def == drug))
                {
                    output[drug] = output.GetValueOrDefault(drug) + p.Pawns.Count;
                }
            }
        }
        return output;
    }
}
