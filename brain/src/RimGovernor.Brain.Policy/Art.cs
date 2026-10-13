namespace RimGovernor.Brain.Policy;

// Go: art.go
// MaintainArt keeps a small sculpture on the way for every room that needs one: a bedroom below its quality
// target whose weakest stat is beauty. Each qualifying artist gets their own fixed-count bill, pinned to them,
// at an art bench; the finished sculpture is installed by the bedroom upkeep's sculpture step.

/// <summary>
/// What an art bill sizes against: the impressiveness gap of the first owed room and the largest square side
/// (1-3) free in it, the colony stock by definition, and each artist's Artistic level. Sculptures to sell are
/// MaintainTrade's (export_orders.go), not art demand.
/// </summary>
public sealed record ArtDemand
{
    public double Gap { get; init; }

    public int Fits { get; init; }

    public IReadOnlyDictionary<Resource, long> Stock { get; init; } = new Dictionary<Resource, long>();

    public IReadOnlyDictionary<PawnId, int> Skill { get; init; } = new Dictionary<PawnId, int>();

    /// <summary>Rank the stuffs a sculpture takes and price a sale.</summary>
    public ItemFacts Items { get; init; } = new();

    /// <summary>The best stocked stuff (beauty factor times market value, as beds choose) with enough for one sculpture of size, false when none.</summary>
    internal bool Stuff(Sculpture size, out Resource stuff)
    {
        var materials = new BedMaterials
        {
            Stock = Stock,
            Cost = new Dictionary<Resource, long> { [new Resource(size.Def)] = size.Cost },
            Items = Items,
        };
        var (best, ok) = materials.BestStuff(new Resource(size.Def), 0, true);
        stuff = best;
        return ok;
    }
}

/// <summary>When an art bill may choose the sculpture of a rank.</summary>
internal readonly record struct SculptureRankGate(double MinGap, int MinSkill);

internal sealed record SculptureRoom(string Id, FurnitureRoom Room, double Gap);

public static class ArtExtensions
{
    /// <summary>The larger side of the sculpture's footprint.</summary>
    public static int Side(this Sculpture s) => Math.Max(s.Size.X, s.Size.Z);

    /// <summary>The North footprint of a sculpture definition, false when the catalog has no sculpture recipe making it.</summary>
    public static bool SculptureSize(this ItemFacts i, string def, out Cell size)
    {
        foreach (var s in i.Sculptures)
        {
            if (s.Def == def)
            {
                size = s.Size;
                return true;
            }
        }

        size = default;
        return false;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// MaintainArt keeps a small sculpture on the way for every room that needs one.
    /// </summary>
    public static readonly ConcernId MaintainArt = new("MaintainArt");

    /// <summary>Ranks MaintainArt with the other upkeep projects.</summary>
    internal const int ArtPriority = 3;

    /// <summary>MaintainArt's bill purpose: one small sculpture pinned to the artist named in ProductionBillContext.Artists.</summary>
    public static readonly BillPurpose ArtBill = new("art");

    /// <summary>The InspirationDef an inspired artist carries; their next art piece gets a quality boost.</summary>
    public const string InspiredCreativity = "Inspired_Creativity";

    /// <summary>Whether a pawn qualifies for an art bill: Artistic above 6 or any Artistic passion, and able to do Art at all.</summary>
    public static bool Artist(PawnProfile p)
    {
        if (p.Incapable.GetValueOrDefault(WorkType.Art) || p.Child)
        {
            return false;
        }

        var s = p.Skill(p.WorkSkill[WorkType.Art]);

        // The wire names no passion "None" (Passion.ToString()).
        return !s.Disabled && (s.Level > 6 || (s.Passion.Length != 0 && s.Passion != "None"));
    }

    /// <summary>The qualifying artists in ID order.</summary>
    public static IReadOnlyList<PawnId> Artists(IEnumerable<PawnProfile> profiles) => [.. profiles.Where(Artist).Select(p => p.Id).Order()];

    /// <summary>One pinned sculpture bill for every artist that lacks an active one, in artist order, each through SelectProductionBill.</summary>
    public static IReadOnlyList<BillSelection> SelectArtBills(
        Fact<EquatableArray<ProductionBench>> benches,
        Fact<long> colonists,
        IEnumerable<PawnId> artists,
        ArtDemand demand
    )
    {
        var output = new List<BillSelection>();
        foreach (var artist in artists)
        {
            var (s, ok) = SelectProductionBill(
                ArtBill,
                benches,
                colonists,
                default,
                default,
                1,
                new ProductionBillContext { Artists = EquatableArray.From([artist]), Art = demand }
            );
            if (ok)
            {
                output.Add(s);
            }
        }

        return output;
    }

    /// <summary>
    /// The priority bill an inspired artist gets: the second-smallest sculpture, the large one (read from the
    /// catalog's sculpture rows by rank). Grand sculptures wait for the stuff choice.
    /// </summary>
    internal static bool InspiredSculpture(ItemFacts items, out Sculpture sculpture)
    {
        if (items.Sculptures.Count < 2)
        {
            sculpture = new Sculpture();
            return false;
        }

        sculpture = items.Sculptures[1];
        return true;
    }

    /// <summary>The qualifying artists with Inspired_Creativity, in ID order.</summary>
    public static IReadOnlyList<PawnId> InspiredArtists(IEnumerable<PawnProfile> profiles) =>
        [.. profiles.Where(p => p.Inspiration.TryGetValue(out var inspiration) && inspiration == InspiredCreativity && Artist(p)).Select(p => p.Id).Order()];

    /// <summary>
    /// One large sculpture bill pinned to every inspired artist lacking an active one. The caller orders these
    /// ahead of SelectArtBills so the inspiration is spent before it expires.
    /// </summary>
    public static IReadOnlyList<BillSelection> SelectInspiredArtBills(Fact<EquatableArray<ProductionBench>> benches, IEnumerable<PawnId> inspired, ItemFacts items)
    {
        var known = benches.TryGetValue(out var rows);
        var lk = InspiredSculpture(items, out var large);
        if (!known || !lk)
        {
            return [];
        }

        var output = new List<BillSelection>();
        foreach (var artist in inspired)
        {
            if (!FoodId(artist.Value))
            {
                continue;
            }

            if (SelectPinnedSculpture(rows, artist, large.Recipe, out var s))
            {
                output.Add(s);
            }
        }

        return output;
    }

    /// <summary>
    /// Picks the art bench, size and stuff for one artist's sculpture bill, none while the artist already has an
    /// active sculpture bill pinned to them.
    /// </summary>
    internal static bool SelectArtBill(IReadOnlyList<ProductionBench> benches, PawnId artist, ArtDemand demand, out BillSelection selection)
    {
        selection = new BillSelection();
        var (available, pinned) = PinnedSculptureOptions(benches, artist);
        if (pinned.Count > 0)
        {
            return false;
        }

        for (var rank = demand.Items.Sculptures.Count - 1; rank >= 0; rank--)
        {
            var size = demand.Items.Sculptures[rank];
            if (!available.TryGetValue(size.Recipe, out var options) || options.Count == 0)
            {
                continue;
            }

            var ok = demand.Stuff(size, out var stuff);
            var gate = SculptureGate(rank);
            if (rank > 0 && (!ok || demand.Gap < gate.MinGap || demand.Fits < size.Side() || demand.Skill.GetValueOrDefault(artist) < gate.MinSkill))
            {
                continue;
            }

            var first = options[0];
            if (ok)
            {
                first = first with { Ingredients = EquatableArray.From([stuff.Value]) };
            }

            selection = first;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Picks the art bench for one artist's recipe bill, none while the artist already has an active bill for
    /// recipe pinned to them.
    /// </summary>
    internal static bool SelectPinnedSculpture(IReadOnlyList<ProductionBench> benches, PawnId artist, string recipeName, out BillSelection selection)
    {
        var (available, pinned) = PinnedSculptureOptions(benches, artist);
        if (pinned.Contains(recipeName) || !available.TryGetValue(recipeName, out var options) || options.Count == 0)
        {
            selection = new BillSelection();
            return false;
        }

        selection = options[0];
        return true;
    }

    /// <summary>
    /// The bills (by recipe, bench order) the artist could be given and the sculpture recipes of the active
    /// bills already pinned to them.
    /// </summary>
    internal static (Dictionary<string, List<BillSelection>> Available, HashSet<string> Pinned) PinnedSculptureOptions(
        IReadOnlyList<ProductionBench> benches,
        PawnId artist
    )
    {
        var available = new Dictionary<string, List<BillSelection>>(StringComparer.Ordinal);
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        if (!FoodId(artist.Value))
        {
            return (available, pinned);
        }

        foreach (var bench in benches)
        {
            foreach (var bill in bench.Bills)
            {
                var wk = bill.Worker.TryGetValue(out var worker);
                var ak = bill.Active.TryGetValue(out var active);
                if (bill.Role == RecipeRole.Sculpture && wk && worker == artist.Value && (!ak || active))
                {
                    pinned.Add(bill.Recipe);
                }
            }

            var uk = bench.Usable.TryGetValue(out var usable);
            var tk = bench.Token.TryGetValue(out var token);
            if (!uk || !usable || !tk || !FoodId(token!) || bench.Bills.Count >= 15)
            {
                continue;
            }

            foreach (var recipe in bench.Recipes)
            {
                if (recipe.Available.TryGetValue(out var ok) && recipe.Role == RecipeRole.Sculpture && ok)
                {
                    if (!available.TryGetValue(recipe.Name, out var list))
                    {
                        list = [];
                        available[recipe.Name] = list;
                    }

                    list.Add(
                        new BillSelection
                        {
                            Bench = bench.Id,
                            Recipe = recipe.Name,
                            Token = token!,
                            Mode = BillMode.GearBatch,
                            Target = 1,
                            Worker = artist.Value,
                        }
                    );
                }
            }
        }

        foreach (var options in available.Values)
        {
            var sorted = PolicySort.ByLess(options, (a, b) => string.CompareOrdinal(a.Bench, b.Bench) < 0);
            options.Clear();
            options.AddRange(sorted);
        }

        return (available, pinned);
    }

    /// <summary>
    /// When an art bill may choose the sculpture of a rank, rank 0 the smallest in the catalog's rows
    /// (ItemFacts.Sculptures): the owed room's impressiveness gap, a free square the size of the footprint,
    /// stock for it, and an artist skill that expects at least a Normal (rank 1) or Good (rank 2 and up) piece.
    /// The smallest sculpture is always allowed, in the best stocked stuff when there is one. The gates are
    /// planner policy by rank, not game numbers.
    /// </summary>
    internal static SculptureRankGate SculptureGate(int rank) =>
        rank switch
        {
            <= 0 => new SculptureRankGate(0, 0),
            1 => new SculptureRankGate(10, 6),
            _ => new SculptureRankGate(25, 10),
        };

    /// <summary>Reads the art demand from the first owed sculpture room and the artists' profiles.</summary>
    public static ArtDemand NewArtDemand(
        Fact<SleepingObservation> obs,
        IReadOnlyDictionary<string, RoomTarget>? targets,
        IReadOnlyList<FurnitureRoom> rooms,
        IReadOnlyDictionary<Resource, long> stock,
        IEnumerable<PawnProfile> profiles,
        ItemFacts items
    )
    {
        var skill = new Dictionary<PawnId, int>();
        foreach (var p in profiles)
        {
            skill[p.Id] = p.Skill(p.WorkSkill[WorkType.Art]).Level;
        }

        var d = new ArtDemand
        {
            Stock = stock,
            Skill = skill,
            Items = items,
        };
        if (!obs.TryGetValue(out var o) || targets is null)
        {
            return d;
        }

        var due = SculptureRooms(o, targets, rooms);
        if (due.Count == 0)
        {
            return d;
        }

        d = d with { Gap = due[0].Gap, Fits = 1 };
        var biggest = 1;
        foreach (var s in items.Sculptures)
        {
            biggest = Math.Max(biggest, s.Side());
        }

        for (var side = biggest; side > 1; side--)
        {
            var (_, _, ok) = FreeSpotFacing(due[0].Room, new Cell(side, side), Rotation.North);
            if (ok)
            {
                d = d with { Fits = side };
                break;
            }
        }

        return d;
    }

    /// <summary>The review's art deficit input: known true while a bedroom below target, weakest in beauty, has a free cell for a sculpture.</summary>
    public static Fact<bool> SculptureRoomsOwed(Fact<SleepingObservation> obs, IReadOnlyDictionary<string, RoomTarget>? targets, IReadOnlyList<FurnitureRoom> rooms)
    {
        if (!obs.TryGetValue(out var o) || targets is null)
        {
            return Unknown<bool>();
        }

        return Known(SculptureRooms(o, targets, rooms).Count > 0);
    }

    /// <summary>The beauty rooms (by id) with a free cell, each with its impressiveness gap.</summary>
    internal static List<SculptureRoom> SculptureRooms(SleepingObservation obs, IReadOnlyDictionary<string, RoomTarget> targets, IReadOnlyList<FurnitureRoom> rooms)
    {
        var output = new List<SculptureRoom>();
        var gaps = new Dictionary<string, double>(StringComparer.Ordinal);
        if (obs.Rooms.TryGetValue(out var census))
        {
            foreach (var r in census)
            {
                if (r.Quality.TryGetValue(out var q))
                {
                    var min = targets.TryGetValue(r.Id, out var target) ? target.Min : 0;
                    gaps[r.Id] = min - q.Impressiveness;
                }
            }
        }

        foreach (var id in BeautyRooms(obs, targets))
        {
            foreach (var room in rooms)
            {
                if (room.Id != id)
                {
                    continue;
                }

                var (_, _, ok) = FreeSpotFacing(room, new Cell(1, 1), Rotation.North);
                if (ok)
                {
                    output.Add(new SculptureRoom(id, room, gaps.GetValueOrDefault(id)));
                }

                break;
            }
        }

        return output;
    }
}
