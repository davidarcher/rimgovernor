namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>
    /// The Sanitation department's incinerator concern: it shells the waste yard and the incinerator inside
    /// it, burns a full incinerator and cleans the ash (rounds_incineration*.go). It is a Standard whose
    /// target is no shell, burn or ash owed. Its zone is a declared Sanitation store
    /// (IncinerationOwner), applied by MaintainStockpiles.
    /// </summary>
    public static readonly ConcernId MaintainIncineration = new("MaintainIncineration");
}

/// <summary>
/// The Sanitation department's stores: the waste yard's one dump zone from plan time, and the incinerator
/// zone once its walls stand.
/// </summary>
public sealed class IncinerationOwner : IStoreOwner
{
    public Department Department => Department.Sanitation;

    /// <summary>
    /// The dump covers the waste yard interior outside the incinerator room's outline at Low priority,
    /// taking all storable items but the native not-burnable ones. The incinerator's whole interior is
    /// Preferred, above the dump so waste hauls in from it, taking what the native rule calls burnable.
    /// </summary>
    public IReadOnlyList<Store> Stores(StoreView view)
    {
        var output = new List<Store>();
        var (dump, ok) = WasteDumpSite(view.Layout);
        if (ok)
        {
            output.Add(new Store { Site = dump! });
        }
        if (view.Incinerator is not null)
        {
            output.Add(new Store
            {
                Site = new StoreSite
                {
                    Role = DomainConstants.IncineratorRole,
                    Interior = view.Incinerator.Region.Bounds(),
                    Filter = StockpileFilter.IncineratorFilter(),
                    Priority = StockpilePriority.Preferred,
                    Exact = true,
                },
            });
        }
        return output;
    }

    public RoomDemand RoomDemand(StoreView view) => DeclaredDemand(view, Stores(view));

    /// <summary>
    /// The dump's site: the first planned waste yard's interior less the planned incinerator's outline (its
    /// walls and interior). It reads the plan, so the zone stands before the yard is fenced.
    /// </summary>
    internal static (StoreSite? Site, bool Ok) WasteDumpSite(LayoutPlan? plan)
    {
        if (plan is null)
        {
            return (null, false);
        }
        var yards = plan.RoomsOf(PlannedRole.WasteYard);
        if (yards.Count == 0)
        {
            return (null, false);
        }
        var yard = yards[0].Region.Bounds();
        var cells = RectCells(yard).ToList();
        foreach (var inc in plan.RoomsOf(PlannedRole.Incinerator))
        {
            var outline = CellSet(RectCells(Pad(inc.Region.Bounds(), 1)));
            cells.RemoveAll(outline.Contains);
        }
        return (new StoreSite
        {
            Role = DomainConstants.DumpRole,
            Interior = yard,
            Filter = StockpileFilter.DumpFilter(),
            Priority = StockpilePriority.Low,
            Regions = CellRects(CellSet(cells)),
            Exact = true,
        }, true);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>Unknown without the plan and construction census, and then it raises no goal.</summary>
    internal static void InspectIncineration(RoundsRun c)
    {
        c.Assess(MaintainIncineration, 3, NotFact(c.F.IncinerationOwed));
        if (c.F.IncinerationOwed.TryGetValue(out var owed) && owed)
        {
            c.Raise(MaintainIncineration, 3);
        }
    }
}
