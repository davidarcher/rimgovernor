namespace RimGovernor.Brain.Policy;

// The herd's stores (task 6.2): one pen feed store per pen unit, inside the unit's first planned pen, holding only the
// pasture-edible feed lying outside the pens (PenFeedStock), so hauling carries it to where the penned animals can eat
// it. A pen's roamers eat only what lies in their enclosure. The store is sized by the unit's feed over the horizon
// (HerdArrangementRules.FeedAsks), keyed by the unit, and retired once the pen's pasture covers the horizon.
internal sealed class HerdOwner : IStoreOwner
{
    public RoomDemand RoomDemand(StoreView view) => new();

    public IReadOnlyList<Store> Stores(StoreView view)
    {
        var output = new List<Store>();
        foreach (var ask in view.PenFeed)
        {
            var role = StockpileRolesConstants.PenFeedRolePrefix + ask.Herd;
            if (ask.Retired)
            {
                output.Add(new Store { Retired = true, Site = new StoreSite { Role = role, Exact = true } });
                continue;
            }
            var (w, h) = HerdPenRules.PenSide(ask.Cells);
            output.Add(new Store
            {
                Site = new StoreSite
                {
                    Role = role,
                    Exact = true,
                    Regions = LayoutPerimeterEnclosureRules.CellRects([.. ask.Pen.Cells()]),
                    Width = w,
                    Height = h,
                    Anchor = ask.Pen.Anchor(),
                    Filter = StockpileFilter.AllowOnlyFilter(ask.Defs),
                    Priority = StockpilePriority.Important,
                },
            });
        }
        return output;
    }
}
