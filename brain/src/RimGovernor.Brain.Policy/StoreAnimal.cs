namespace RimGovernor.Brain.Policy;

/// <summary>
/// The herds' feed store: the feed their races eat and the animal sleeping spot the barn's template is planned
/// with, which the store is sited beside.
/// </summary>
public sealed record AnimalFeedStore
{
    public IReadOnlyList<Resource> Feed { get; init; } = [];

    public InteriorPieceDef Spot { get; init; } = new();
}

/// <summary>
/// The People department's animal store: the barn's feed stockpile, a small feed-filtered zone at Important
/// priority in each planned barn's free floor beside the sleeping spots. A herd with no barn has none: its feed
/// is held by the warehouse or the freezer.
/// </summary>
internal sealed class AnimalOwner : IStoreOwner
{
    public Department Department => Department.People;

    public IReadOnlyList<Store> Stores(StoreView v)
    {
        if (v.Layout is null || v.AnimalFeed is null || v.AnimalFeed.Feed.Count == 0)
        {
            return [];
        }
        StockpileFilter filter;
        try
        {
            filter = StockpileFilter.AllowOnlyFilter(v.AnimalFeed.Feed.Select(def => def.Value));
        }
        catch (DomainException)
        {
            return [];
        }
        var output = new List<Store>();
        foreach (var barn in v.Layout.HerdRooms(PlannedRole.Barn))
        {
            var avoid = new List<Cell>();
            var (room, ok) = PolicyFunctions.InteriorRoomFromLayout(barn, v.Shapes);
            if (ok)
            {
                var (plan, planned) = PolicyFunctions.PlanRoomInterior(room!, v.AnimalFeed.Spot);
                if (planned)
                {
                    foreach (var p in plan!.Pieces)
                    {
                        avoid.AddRange(PolicyFunctions.RectCells(p.Rect));
                    }
                }
            }
            var bounds = barn.Region.Bounds();
            output.Add(new Store
            {
                Site = new StoreSite
                {
                    Role = PolicyFunctions.PlannedKey(PolicyFunctions.FeedRolePrefix, bounds),
                    Interior = bounds,
                    Width = PolicyFunctions.FeedStoreWidth,
                    Height = PolicyFunctions.FeedStoreHeight,
                    Anchor = barn.Door,
                    Avoid = avoid,
                    Filter = filter,
                    Priority = StockpilePriority.Important,
                },
            });
        }
        return output;
    }

    public RoomDemand RoomDemand(StoreView v) => new();
}

public static partial class PolicyFunctions
{
    /// <summary>Keys a barn's feed stockpile: "feed:&lt;barn interior corner&gt;".</summary>
    public const string FeedRolePrefix = "feed:";

    /// <summary>
    /// Size the barn feed store once: a 1x2 strip, the largest that fits the aisle between the template's rows
    /// of spots, in the barn's free floor, never resized.
    /// </summary>
    public const int FeedStoreWidth = 1;

    public const int FeedStoreHeight = 2;

    /// <summary>
    /// The feed the animals' races eat that a bench makes or a field grows (hay), sorted: what the barn feed
    /// store accepts. Empty while the races are not in the catalog.
    /// </summary>
    public static List<Resource> AnimalFeedFilterOf(IReadOnlyList<UpkeepAnimal> animals, AnimalRaceCatalog races)
    {
        var seen = new HashSet<Resource>();
        foreach (var a in animals)
        {
            var known = races.Race(a.Definition, out var race);
            if (!known)
            {
                continue;
            }
            foreach (var item in race!.FeedItems)
            {
                seen.Add(item.Def);
            }
            if (race.Edible.Contains(HayResource.Value))
            {
                seen.Add(HayResource);
            }
        }
        return [.. seen.OrderBy(r => r.Value, StringComparer.Ordinal)];
    }
}
