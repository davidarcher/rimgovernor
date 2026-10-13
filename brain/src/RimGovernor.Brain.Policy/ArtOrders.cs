namespace RimGovernor.Brain.Policy;

// Go: art_orders.go
// MaintainArt declares its pinned sculpture batches to the work ledger (OrderDeclarer): the ledger places a
// missing one, keeps a standing one and removes the rest, so a sculpture pinned to someone who is no longer an
// artist is simply no longer declared. Sculptures to sell are MaintainTrade's.

/// <summary>
/// What one Round's sculpture declaration reads: the bench readback the ledger reconciles against, the work
/// pawns' profiles, the art demand and whether a bedroom still wants a sculpture.
/// </summary>
public sealed record ArtOrderRequest
{
    public EquatableArray<GearBench> Benches { get; init; }

    public Fact<EquatableArray<PawnProfile>> Profiles { get; init; }

    public Fact<long> Colonists { get; init; }

    public required ItemFacts Items { get; init; }

    public required ArtDemand Demand { get; init; }

    public Fact<bool> RoomsOwed { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// MaintainArt's wanted sculpture orders. While the need is open (an inspired artist or a bedroom below
    /// target) every active sculpture bill pinned to a current artist is declared as it stands, and each artist
    /// lacking one is given the bill the art selectors choose; a need known to be gone declares nothing, so the
    /// ledger removes the sculptures. Abstain while the artists or the need are unread, or a standing sculpture
    /// bill's spec is.
    /// </summary>
    public static Declared DeclareArtOrders(ArtOrderRequest r)
    {
        if (!r.Profiles.TryGetValue(out var profiles))
        {
            return Abstaining(Cause.UnreadArtists);
        }

        var artists = Artists(profiles);
        var inspired = InspiredArtists(profiles);
        var owedKnown = r.RoomsOwed.TryGetValue(out var owed);
        var sculpt = owedKnown && owed;
        var open = sculpt || inspired.Count > 0;
        if (!open && owedKnown)
        {
            return new Declared();
        }

        var (orders, unread) = StandingSculptures(r.Benches, artists);
        var output = new Declared();
        output.Orders.AddRange(orders);
        if (unread)
        {
            output.Unread(Cause.UnreadBills);
        }

        if (!open)
        {
            output.Unread(Cause.UnreadArtNeed);
        }

        var benches = Known(EquatableArray.From(ArtProductionBenches(r.Benches)));
        var selected = new List<BillSelection>(SelectInspiredArtBills(benches, inspired, r.Items));
        if (sculpt)
        {
            selected.AddRange(SelectArtBills(benches, r.Colonists, artists, r.Demand));
        }

        foreach (var s in selected)
        {
            output.Orders.Add(
                new OrderSpec
                {
                    Recipe = s.Recipe,
                    Ingredients = EquatableArray.From(s.Ingredients),
                    Worker = s.Worker,
                    Mode = s.Mode,
                    Target = s.Target,
                    BenchKind = GearBenchDef(r.Benches, s.Bench),
                }
            );
        }

        return output;
    }

    /// <summary>
    /// The specs of the active (or unknown-active) sculpture bills pinned to a current artist; unread is set
    /// when one's spec is.
    /// </summary>
    internal static (List<OrderSpec> Orders, bool Unread) StandingSculptures(IEnumerable<GearBench> benches, IEnumerable<PawnId> artists)
    {
        var orders = new List<OrderSpec>();
        var unread = false;
        var current = new HashSet<string>(artists.Select(a => a.Value), StringComparer.Ordinal);
        foreach (var b in benches)
        {
            b.Bills.TryGetValue(out var bills);
            foreach (var bill in bills!)
            {
                var wk = bill.Worker.TryGetValue(out var worker);
                if (bill.Role != RecipeRole.Sculpture || !wk || !current.Contains(worker!))
                {
                    continue;
                }

                if (bill.Active.TryGetValue(out var active) && !active)
                {
                    continue;
                }

                if (!bill.Spec.TryGetValue(out var spec))
                {
                    unread = true;
                    continue;
                }

                orders.Add(spec);
            }
        }

        return (orders, unread);
    }

    /// <summary>
    /// The benches offering a sculpture recipe as the art selectors read them; a bench whose recipes or bills
    /// are unknown is left out. The ledger chooses the bench when it places a declared order, so the bench id
    /// stands in for the write token no declaration needs.
    /// </summary>
    public static IReadOnlyList<ProductionBench> ArtProductionBenches(IEnumerable<GearBench> benches)
    {
        var output = new List<ProductionBench>();
        foreach (var read in benches)
        {
            var rk = read.Recipes.TryGetValue(out var recipes);
            var bk = read.Bills.TryGetValue(out var bills);
            if (!rk || !bk)
            {
                continue;
            }

            var rows = new List<ProductionRecipe>();
            foreach (var recipe in recipes!)
            {
                if (recipe.Role != RecipeRole.Sculpture)
                {
                    continue;
                }

                var ak = recipe.Available.TryGetValue(out var available);
                var ok = recipe.AvailableOn.TryGetValue(out var on);
                var row = new ProductionRecipe { Name = recipe.Definition, Role = recipe.Role };
                if (ak && ok)
                {
                    row = row with { Available = Known(available && on) };
                }

                rows.Add(row);
            }

            if (rows.Count == 0)
            {
                continue;
            }

            var existing = bills!.Select(bill => new ExistingProductionBill
            {
                Id = bill.Id,
                Recipe = bill.Recipe,
                Role = bill.Role,
                Worker = bill.Worker,
                Active = bill.Active,
            });
            output.Add(
                new ProductionBench
                {
                    Id = read.Id,
                    Token = Known(read.Id),
                    Usable = Known(true),
                    Recipes = EquatableArray.From(rows),
                    Bills = EquatableArray.From(existing),
                }
            );
        }

        return output;
    }

    /// <summary>
    /// Whether any sculpture bill is active: the sculpture takes days of game time that a placed bill does not
    /// ask the clock for.
    /// </summary>
    public static bool SculptureInProgress(IEnumerable<GearBench> benches)
    {
        foreach (var b in benches)
        {
            b.Bills.TryGetValue(out var bills);
            foreach (var bill in bills!)
            {
                if (bill.Active.TryGetValue(out var active) && active && bill.Role == RecipeRole.Sculpture)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
