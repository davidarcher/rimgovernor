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

public static partial class PolicyFunctions
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
    public static (Fact<EquatableArray<LootItem>> Kept, IReadOnlyList<LootHold> Holds) FilterLootReach(
        Fact<EquatableArray<LootItem>> observed, RemoteWorkRequest r)
    {
        if (!observed.TryGetValue(out var rows))
        {
            return (observed, []);
        }
        var reasons = new string[rows.Count];
        Array.Fill(reasons, "");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.SafetyKnown || !row.SafeToHaul || !row.Forbidden || LootInsideExtent(r.Extent, row.Supply.Cell))
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
        return (Known(EquatableArray.From(kept)), holds.OrderBy(h => h.Thing, StringComparer.Ordinal).ToList());
    }

    internal static bool LootInsideExtent(Fact<ColonyExtent> extent, Cell cell)
    {
        if (!extent.TryGetValue(out var e))
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
    /// eligibility and route evidence: the census only reports SafeToHaul after walking a colonist's route.
    /// Distance is the supply planner's price, not a hold.
    /// </summary>
    internal static string RemoteLootReachHold(RemoteWorkRequest r) => RemoteThreatHold(r.Safety);

    /// <summary>
    /// A stack as a supply candidate: its census count, hauled to the storage headroom the census reports,
    /// no labor beyond the haul.
    /// </summary>
    internal static SupplyCandidate LootCandidate(LootItem row) =>
        SourceCandidate(CandidateKind.Loot, row.Supply.Thing, Known(0.0), row.PathLength, true, LootUnitsPerTrip,
            SourceYield(new ResourceKey { Def = new Resource(row.Supply.Definition) }, row.Count, 0, row.StorageHeadroom));

    /// <summary>
    /// The remote request the loot filter runs against: safety and the derived extent from the review facts,
    /// and the urgent work competing for the colonists.
    /// </summary>
    public static RemoteWorkRequest LootContext(RoundsFacts f)
    {
        var extent = DeriveColonyExtent(new ColonyExtentRequest { Bounds = f.MapBounds, Construction = f.CurrentConstruction, Claims = f.ConstructionClaims, Home = f.HomeCoverage });
        return new RemoteWorkRequest { Safety = LootSafety(f), Extent = extent, Competition = RemoteCompetition(f) };
    }

    /// <summary>Assembles the outdoor safety from the review facts. Unknown hostiles stay unknown.</summary>
    public static RemoteSafety LootSafety(RoundsFacts f)
    {
        var r = new RemoteSafety { Hazard = OutdoorHazardsOf(f), DashOpen = f.DashOpen() };
        if (f.Hostiles.TryGetValue(out var hostiles))
        {
            r = r with { Threat = Known(hostiles > 0) };
        }
        return r;
    }
}
