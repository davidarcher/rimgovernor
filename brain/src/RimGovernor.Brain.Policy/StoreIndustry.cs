namespace RimGovernor.Brain.Policy;

// The Industry department's stores: one ingredient stockpile per bench that works a standing bill (stone chunks
// at the stonecutter, ingredients at every other bench; the kitchen's and butcher's benches are the food
// stores'), a free roofed 2x2 patch in the bench's room nearest the bench, holding that bench's recipe inputs at
// a priority above the general store so hauling brings them to the bench. It is keyed by bench id, sized once,
// and retired only when its bench is gone.
internal sealed class IndustryOwner : IStoreOwner
{
    public Department Department => Department.Industry;

    /// <summary>The side of a bench store's square patch.</summary>
    private const int BenchStoreSize = 2;

    public IReadOnlyList<Store> Stores(StoreView view)
    {
        var output = new List<Store>();
        if (view.Rooms is not null)
        {
            foreach (var input in view.BenchInputs)
            {
                var (room, ok) = RoomHolding(view.Rooms.Rooms, input.Cell);
                if (!ok)
                {
                    continue;
                }
                StockpileFilter filter;
                try
                {
                    filter = StockpileFilter.AllowOnlyFilter(input.Inputs);
                }
                catch (DomainException)
                {
                    continue;
                }
                output.Add(new Store
                {
                    Site = new StoreSite
                    {
                        Role = DomainConstants.IngredientsPrefix + input.Bench,
                        Exact = true,
                        Regions = PolicyFunctions.CellRects(PolicyFunctions.CellSet(room!.Cells)),
                        Width = BenchStoreSize,
                        Height = BenchStoreSize,
                        Roofed = true,
                        Anchor = input.Cell,
                        Filter = filter,
                        Priority = StockpilePriority.Important,
                    },
                });
            }
        }
        output.AddRange(RetiredBenchStores(view));
        return output;
    }

    /// <summary>The stores whose standing zone names a bench the census no longer lists. An unread census retires nothing.</summary>
    private static List<Store> RetiredBenchStores(StoreView view)
    {
        if (!view.Benches.TryGetValue(out var standing))
        {
            return [];
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<Store>();
        foreach (var z in view.Zones)
        {
            if (!z.Role.StartsWith(DomainConstants.IngredientsPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var bench = z.Role[DomainConstants.IngredientsPrefix.Length..];
            if (bench.Length == 0 || (standing.TryGetValue(bench, out var present) && present) || !seen.Add(z.Role))
            {
                continue;
            }
            output.Add(new Store { Site = new StoreSite { Role = z.Role, Exact = true }, Retired = true });
        }
        return [.. output.OrderBy(s => s.Role, StringComparer.Ordinal)];
    }

    public RoomDemand RoomDemand(StoreView v) => PolicyFunctions.DeclaredDemand(v, Stores(v));

    private static (Room? Room, bool Ok) RoomHolding(IReadOnlyList<Room> rooms, Cell cell)
    {
        foreach (var room in rooms)
        {
            foreach (var c in room.Cells)
            {
                if (c == cell)
                {
                    return (room, true);
                }
            }
        }
        return (null, false);
    }
}
