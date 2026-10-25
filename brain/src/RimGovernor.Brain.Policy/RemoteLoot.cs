namespace RimGovernor.Brain.Policy;

/// <summary>
/// Records a safe, forbidden stack demand scoring or a safety hold kept forbidden, with the reason, so the
/// review can explain it.
/// </summary>
public sealed record LootHold
{
    public string Thing { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public string Reason { get; init; } = "";
}

public static partial class RemoteRules
{
    /// <summary>A colonist's ordinary carrying capacity in item units.</summary>
    internal const int LootUnitsPerTrip = 75;

    /// <summary>
    /// Preserves loot safety and narrows only the Allow side: an unsafe stack still forbids, an already allowed
    /// stack is not re-forbidden by reach, and a safe forbidden stack inside the established extent is allowed
    /// as before. A safe forbidden stack outside the extent is allowed when no safety hold applies, whatever
    /// the colony's demand, storage headroom or competing work: Allow only clears a flag and dispatches no
    /// pawn, so the storage and urgent-work holds of dispatched remote work do not apply. A safety hold is
    /// reported with an explicit reason. The returned rows are the census the safety review should act on.
    /// </summary>
    public static (EquatableArray<LootItem> Kept, IReadOnlyList<LootHold> Holds) FilterLootReach(
        EquatableArray<LootItem> rows, RemoteWorkRequest r)
    {
        var reasons = new string[rows.Count];
        Array.Fill(reasons, "");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.IsSafe || !row.Forbidden || LootInsideExtent(r.Extent, row.Supply.Cell))
            {
                continue;
            }
            reasons[i] = RemoteLootReachHold(r);
        }
        var kept = new List<LootItem>();
        var holds = new List<LootHold>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (reasons[i].Length == 0)
            {
                kept.Add(row);
                continue;
            }
            holds.Add(new LootHold { Thing = row.Supply.Thing, Definition = row.Supply.Definition, Cell = row.Supply.Cell, Reason = reasons[i] });
        }
        return (EquatableArray.From(kept), holds.OrderBy(h => h.Thing, StringComparer.Ordinal).ToList());
    }

    internal static bool LootInsideExtent(ExtentDerivation extent, Cell cell)
    {
        if (extent is not ExtentDerivation.Derived { Extent: var e })
        {
            return false;
        }
        foreach (var region in e.Regions)
        {
            foreach (var c in region.Cells)
            {
                if (c.Cell == cell)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the empty string when no safety hold applies. The native safety verdict is this candidate's
    /// eligibility and route evidence: the census only reports a Safe haul after walking a colonist's route.
    /// Distance is the supply planner's price, not a hold.
    /// </summary>
    internal static string RemoteLootReachHold(RemoteWorkRequest r) => RemoteThreatHold(r.Safety);

    /// <summary>
    /// The remote request the loot filter runs against: safety and the derived extent from the review facts,
    /// and the urgent work competing for the colonists.
    /// </summary>
    public static RemoteWorkRequest LootContext(RoundsFacts f)
    {
        var extent = ColonyExtentRules.DeriveColonyExtent(f.MapBounds, f.CurrentConstruction, f.ConstructionClaims, f.HomeCoverage);
        return new RemoteWorkRequest { Safety = LootSafety(f), Extent = extent, Competition = RemoteCompetition(f) };
    }

    /// <summary>Assembles outdoor safety from the required hostile census.</summary>
    public static RemoteSafety LootSafety(RoundsFacts f)
    {
        var r = new RemoteSafety { Hazard = OutdoorHazardRules.OutdoorHazardsOf(f), DashOpen = f.DashOpen() };
        if (f.Hostiles.TryGetValue(out var hostiles))
        {
            r = r with { Threat = hostiles > 0 };
        }
        else { throw new RequiredFactException("remote hostile census"); }
        return r;
    }
}
