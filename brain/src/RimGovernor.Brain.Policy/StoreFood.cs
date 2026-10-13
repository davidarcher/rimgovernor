using System.Globalization;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The Food department's stockpiles: the meal closet, the table cell, the freezer's shelves and perishables
/// catch-all. Each is declared once and sited from the layout plan at plan time, at its real priority, sized
/// once; only the table cell reads a built fact (the table).
/// </summary>
internal sealed class FoodOwner : IStoreOwner
{
    public Department Department => Department.Food;

    public RoomDemand RoomDemand(StoreView v) => PolicyFunctions.DeclaredDemand(v, Stores(v));

    /// <summary>
    /// Most important first: the meal store, the freezer's Critical shelves nearest the kitchen door (raw meat,
    /// raw vegetables, animal and insect corpses by the butcher's door), then the perishables catch-all over
    /// the rest of the freezer.
    /// </summary>
    public IReadOnlyList<Store> Stores(StoreView v)
    {
        var output = new List<Store>();
        if (v.Layout is not null)
        {
            output.AddRange(v.MealClosets());
        }
        if (v.Meals is not null)
        {
            output.Add(v.TableStore(v.Meals));
        }
        if (v.Layout is not null)
        {
            output.AddRange(v.FreezerMealShelf());
            output.AddRange(v.FreezerStores());
        }
        return output;
    }
}

public sealed partial record StoreView
{
    /// <summary>The planned meal closets, each covered whole.</summary>
    internal List<Store> MealClosets()
    {
        var output = new List<Store>();
        foreach (var closet in Layout!.Rooms)
        {
            if (closet.Role == PlannedRole.MealCloset)
            {
                var bounds = closet.Region.Bounds();
                output.Add(new Store
                {
                    Site = new StoreSite
                    {
                        Role = PolicyFunctions.PlannedKey(DomainConstants.MealsRolePrefix, bounds),
                        Interior = bounds,
                        Region = closet.Region,
                        Filter = StockpileFilter.MealShelfFilter(),
                        Priority = StockpilePriority.Critical,
                    },
                });
            }
        }
        return output;
    }

    /// <summary>
    /// The one cell of the cooked meal by the dining table, off the chairs. It is the one store sited from a
    /// built fact (the table); its role is exact so retiring it never touches the closet or the freezer shelf.
    /// </summary>
    internal Store TableStore(MealStore meals) =>
        new()
        {
            Retired = meals.Retired,
            Site = new StoreSite
            {
                Role = PolicyFunctions.PlannedKey(DomainConstants.MealsRolePrefix, meals.Dining),
                Interior = meals.Dining,
                Width = 1,
                Height = 1,
                Anchor = meals.Anchor,
                Avoid = meals.Avoid,
                Filter = meals.Filter,
                Priority = StockpilePriority.Critical,
                Exact = true,
            },
        };

    /// <summary>The first planned freezer.</summary>
    internal (PlannedRoom? Room, bool Ok) Freezer()
    {
        foreach (var planned in Layout!.Rooms)
        {
            if (planned.Role == PlannedRole.Freezer)
            {
                return (planned, true);
            }
        }
        return (null, false);
    }

    /// <summary>
    /// The 2x2 meal shelf in the freezer at its door into the dining room, planned with the freezer so the
    /// perishables catch-all never takes its ground.
    /// </summary>
    internal List<Store> FreezerMealShelf()
    {
        foreach (var dining in Layout!.Rooms)
        {
            if (dining.Role != PlannedRole.Dining)
            {
                continue;
            }
            var (freezer, door, ok) = Layout.FreezerDoorInto(dining);
            if (ok)
            {
                var bounds = freezer!.Region.Bounds();
                return
                [
                    new Store
                    {
                        Site = new StoreSite
                        {
                            Role = PolicyFunctions.PlannedKey(DomainConstants.MealsRolePrefix, bounds),
                            Interior = bounds,
                            Width = 2,
                            Height = 2,
                            Anchor = door,
                            Filter = StockpileFilter.MealShelfFilter(),
                            Priority = StockpilePriority.Critical,
                        },
                    },
                ];
            }
            return [];
        }
        return [];
    }

    /// <summary>Dedicated 2x2 shelves nearest the kitchen door, then one lower priority zone over the rest of the freezer taking every perishable.</summary>
    internal List<Store> FreezerStores()
    {
        var (found, ok) = Freezer();
        if (!ok)
        {
            return [];
        }
        var freezer = found!;
        var bounds = freezer.Region.Bounds();
        var anchor = freezer.Door;
        var (link, linked) = Layout!.LinkInto(freezer, PlannedRole.Dining);
        if (linked)
        {
            anchor = link;
        }
        var output = new List<Store>();
        (string Prefix, StockpileFilter Filter)[] shelves =
        [
            (DomainConstants.RawMeatRolePrefix, StockpileFilter.RawMeatFilter()),
            (DomainConstants.RawVegRolePrefix, StockpileFilter.RawVegFilter()),
            (DomainConstants.CorpsesRolePrefix, StockpileFilter.CorpseLarderFilter()),
        ];
        foreach (var shelf in shelves)
        {
            var at = anchor;
            if (shelf.Prefix == DomainConstants.CorpsesRolePrefix)
            {
                // Carcasses stay by the butcher's door into the freezer.
                var (door, hasDoor) = PolicyFunctions.ButcheryDoor(Layout);
                if (hasDoor)
                {
                    at = door;
                }
            }
            output.Add(new Store
            {
                Site = new StoreSite
                {
                    Role = PolicyFunctions.PlannedKey(shelf.Prefix, bounds),
                    Interior = bounds,
                    Width = 2,
                    Height = 2,
                    Anchor = at,
                    Filter = shelf.Filter,
                    Priority = StockpilePriority.Critical,
                },
            });
        }
        output.Add(new Store
        {
            Site = new StoreSite
            {
                Role = PolicyFunctions.PlannedKey(DomainConstants.PerishablesRolePrefix, bounds),
                Interior = bounds,
                Filter = StockpileFilter.PerishablesFilter(),
                Priority = StockpilePriority.Preferred,
            },
        });
        return output;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>Names a planned room's zone role by its interior, the identity the zone is matched to.</summary>
    internal static string PlannedKey(string prefix, Rectangle interior) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix}{interior.X}_{interior.Z}");

    internal static List<Cell> WithoutCells(IEnumerable<Cell> cells, IEnumerable<Cell> drop)
    {
        var skip = CellSet(drop);
        return [.. cells.Where(c => !skip.Contains(c))];
    }
}
