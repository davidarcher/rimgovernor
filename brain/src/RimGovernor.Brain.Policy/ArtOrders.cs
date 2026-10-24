namespace RimGovernor.Brain.Policy;

// The rooms/art Need declares its pinned sculpture batches to the work ledger (OrderDeclarer): the ledger places a
// missing one, keeps a standing one and removes the rest, so a sculpture pinned to someone who is no longer an
// artist is simply no longer declared. Sculptures to sell are the economy/trade Need's.

/// <summary>
/// What one Round's sculpture declaration reads: the bench readback the ledger reconciles against, the work
/// pawns' profiles, the art demand and whether a bedroom still wants a sculpture.
/// </summary>
public sealed record ArtOrderRequest
{
    public EquatableArray<GearBench> Benches { get; init; }

    public Fact<EquatableArray<PawnProfile>> Profiles { get; init; }

    public long Colonists { get; init; }

    public required ItemFacts Items { get; init; }

    public required ArtDemand Demand { get; init; }

    public Fact<bool> RoomsOwed { get; init; }
}

public static class ArtOrdersRules
{
    /// <summary>
    /// The rooms/art Need's wanted sculpture orders. While the need is open (an inspired artist or a bedroom below
    /// target) every active sculpture bill pinned to a current artist is declared as it stands, and each artist
    /// lacking one is given the bill the art selectors choose; a need known to be gone declares nothing, so the
    /// ledger removes the sculptures. Abstain while the artists or the need are unread.
    /// </summary>
    public static Declared DeclareArtOrders(ArtOrderRequest r)
    {
        if (!r.Profiles.TryGetValue(out var profiles))
        {
            return WorkRules.Abstaining(Cause.UnreadArtists);
        }

        var artists = ArtRules.Artists(profiles);
        var inspired = ArtRules.InspiredArtists(profiles);
        var owedKnown = r.RoomsOwed.TryGetValue(out var owed);
        var sculpt = owedKnown && owed;
        var open = sculpt || inspired.Count > 0;
        if (!open && owedKnown)
        {
            return new Declared();
        }

        var output = new Declared();
        output.Orders.AddRange(StandingSculptures(r.Benches, artists));

        if (!open)
        {
            output.Unread(Cause.UnreadArtNeed);
        }

        var benches = ArtProductionBenches(r.Benches);
        var selected = new List<BillSelection>(ArtRules.SelectInspiredArtBills(benches, inspired, r.Items));
        if (sculpt)
        {
            selected.AddRange(ArtRules.SelectArtBills(benches, r.Colonists, artists, r.Demand));
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
                    BenchKind = GearOrdersRules.GearBenchDef(r.Benches, s.Bench),
                }
            );
        }

        return output;
    }

    /// <summary>The specs of the active sculpture bills pinned to a current artist.</summary>
    internal static List<OrderSpec> StandingSculptures(IEnumerable<GearBench> benches, IEnumerable<PawnId> artists)
    {
        var orders = new List<OrderSpec>();
        var current = new HashSet<string>(artists.Select(a => a.Value), StringComparer.Ordinal);
        foreach (var b in benches)
        {
            foreach (var bill in b.Bills)
            {
                if (bill.Role != RecipeRole.Sculpture ||!current.Contains(bill.Worker) || !bill.Active)
                {
                    continue;
                }

                orders.Add(bill.Spec);
            }
        }

        return orders;
    }

    /// <summary>
    /// The benches offering a sculpture recipe as the art selectors read them. The ledger chooses the bench when it
    /// places a declared order, so the bench id stands in for the write token no declaration needs.
    /// </summary>
    public static IReadOnlyList<ProductionBench> ArtProductionBenches(IEnumerable<GearBench> benches)
    {
        var output = new List<ProductionBench>();
        foreach (var read in benches)
        {
            var rows = new List<ProductionRecipe>();
            foreach (var recipe in read.Recipes)
            {
                if (recipe.Role != RecipeRole.Sculpture)
                {
                    continue;
                }

                rows.Add(new ProductionRecipe { Name = recipe.Definition, Role = recipe.Role, Food = new RecipeFood.NotFood(), Available = recipe.Available && recipe.AvailableOn });
            }

            if (rows.Count == 0)
            {
                continue;
            }

            var existing = read.Bills.Select(bill => bill.Existing);
            output.Add(
                new ProductionBench
                {
                    Id = read.Id,
                    Token = read.Id,
                    Usable = true,
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
            foreach (var bill in b.Bills)
            {
                if (bill.Active &&bill.Role == RecipeRole.Sculpture)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
