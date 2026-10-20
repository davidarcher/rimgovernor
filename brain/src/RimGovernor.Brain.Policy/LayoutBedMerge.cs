namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the colony reads for the bed merge's price (#3658): each pool sleeper's capability-days lost a day sleeping in a
/// barracks over a bedroom (BedroomGain at the planned stage minus the barracks gain there), the sleepers whose loss is
/// unread, and the capability-days one wall cell and one door of a planned bedroom's base ring cost to build (labor plus
/// material). An unknown wall or door leaves the merge unpriced.
/// </summary>
public sealed record BedMergeFacts
{
    public EquatableArray<double> Losses { get; init; }

    public int Unpriced { get; init; }

    public Fact<double> WallCell { get; init; }

    public Fact<double> Door { get; init; }
}

/// <summary>Everything the clustering prices a bed merge with (#3658): the colony's facts, the climate the saved room's heat is read in, and the barracks the plan holds.</summary>
public sealed record BedMergePrice
{
    public BedMergeFacts Facts { get; init; } = new();

    public Fact<EquatableArray<double>> OutdoorCurve { get; init; }

    /// <summary>The map is cold or hot (MapSurvey.Cold, Hot): an unknown heat there leaves the merge unpriced, where on a mild map it counts as zero.</summary>
    public bool Harsh { get; init; }

    /// <summary>The watts a colonist's work-day is worth (WorkPrice.WorkDayW); 0 while unknown.</summary>
    public double WorkDayW { get; init; }

    /// <summary>The bed slots of the barracks the plan holds (BedSlotsOf), 0 for none: the incumbent a new size must beat by more than the tie resolution.</summary>
    public int Incumbent { get; init; }

    /// <summary>
    /// The plan's committed barracks and bedrooms, each with what of its shell stands (#3661, #3675): a room standing or
    /// under construction. A size that leaves fewer rooms than these gives up the least spent of them and pays what was
    /// spent on each, so a split being built is not merged back by the next review; a room on paper is free to drop.
    /// </summary>
    public EquatableArray<ShellPieces> Committed { get; init; }
}

/// <summary>The barracks size a free-bed pool takes; Finding names why the merge is unpriced.</summary>
public readonly record struct BarracksChoice(int Size, string Finding);

public static partial class LayoutClusterRules
{
    /// <summary>The free-bed pool's size was priced (#3658): the decision's verdict is merge for a barracks of more than one bed, split otherwise.</summary>
    public const string ReasonClusterBedMerge = "cluster_bed_merge_priced";

    /// <summary>The free-bed pool's merge is not priced (#3658): the beds stay apart. The reason carries the unknown part after a colon.</summary>
    public const string ReasonClusterBedMergeUnpriced = "cluster_bed_merge_unpriced";

    /// <summary>
    /// The barracks size the input's free-bed pool takes (#3658, Clusterer.BarracksSize): null when the graph has no free
    /// pool or the input prices no merge.
    /// </summary>
    public static BarracksChoice? BarracksSizeOf(ClusterInput input)
    {
        var c = new Clusterer(input, MaxRoomArea(input.RoofDistance));
        for (var i = 0; i < input.Graph.Nodes.Count; i++)
        {
            var n = input.Graph.Nodes[i];
            if (n.Purpose == StationPurpose.Sleep && n.Floor == 0 && n.Count > 1 && input.BedMerge is { } price)
            {
                return c.BarracksSize(i, n.Count, price);
            }
        }
        return null;
    }

    /// <summary>
    /// The capability-days of building one shell piece (#3658): its work to build over a day's ticks plus its cost list at
    /// the materials' values (ForecastValue.MaterialValueOf); unknown when the piece's work or cost list is unread.
    /// </summary>
    public static Fact<double> ShellPiecePrice((double Work, IReadOnlyList<Amount> Costs)? piece, IReadOnlyDictionary<Resource, ConstructionMaterialValue> values) =>
        piece is { } p ? Known((p.Work / GameTime.TicksPerDay) + p.Costs.Sum(a => a.Count * values[a.Resource].PerUnit)) : default;
}

internal sealed partial class Clusterer
{
    /// <summary>
    /// Splits the free-bed pool (the Sleep node that is no suite) by its priced bed merge (#3658): a barracks of the chosen
    /// size and the rest single. Null for any other node, or while the input prices no merge, which halves it as before.
    /// </summary>
    private List<ClusterUnit>? BedMergeSplit(int n, int count, List<ClusterDecision> decisions)
    {
        var node = _in.Graph.Nodes[n];
        if (_in.BedMerge is not { } price || node.Purpose != StationPurpose.Sleep || node.Floor > 0 || count < 2)
        {
            return null;
        }
        var choice = BarracksSize(n, count, price);
        decisions.Add(new ClusterDecision
        {
            A = node.Id,
            Verdict = choice.Size > 1 ? LayoutClusterRules.ClusterMerge : LayoutClusterRules.ClusterSplit,
            Reason = choice.Finding.Length > 0 ? choice.Finding : LayoutClusterRules.ReasonClusterBedMerge,
        });
        var output = new List<ClusterUnit> { new(n, choice.Size) };
        for (var i = choice.Size; i < count; i++)
        {
            output.Add(new ClusterUnit(n, 1));
        }
        return output;
    }

    /// <summary>
    /// The barracks size of a pool of count free beds of node n (#3658): the k that maximises (k-1) times the price of one
    /// bedroom saved less the k cheapest sleepers' losses over the horizon (ColonyModelPolicy.HorizonDays), its spare beds
    /// losing nothing and a sleeper whose loss is unread never joining. A size whose room breaks the area limit is not
    /// taken. The incumbent (the plan's barracks, else every bed single) holds unless the best beats it by more than
    /// ForecastValuePolicy.TieResolution. An unpriced saving keeps every bed single.
    /// </summary>
    /// <remarks>
    /// Switching (#3661). A split of a standing barracks keeps the barracks for the beds that stay (the embedding holds it
    /// where it stands for the smaller group) and builds a bedroom per bed taken out, so its cost is that bedroom's shell
    /// and heat, the saving the merge gave up; nothing is demolished. The reverse pays what was spent (#3675): the plan's
    /// barracks and bedrooms that stand or are under construction (Committed) are its rooms, and a size that leaves fewer
    /// rooms gives up the least spent of them and pays the walls and doors standing on each. A built bedroom merged back
    /// saves only its heat, so a mood swinging about the switch line switches once; a bedroom on paper is free to drop,
    /// its flapping held by the tie resolution and the replan key's mood band.
    /// </remarks>
    internal BarracksChoice BarracksSize(int n, int count, BedMergePrice price)
    {
        var single = Eval([new ClusterUnit(n, 1)]);
        var (saved, finding) = SavedPerRoom(single.Area, price);
        if (saved is not { } room)
        {
            return new BarracksChoice(1, finding);
        }
        var f = price.Facts;
        var wall = f.WallCell.Value;
        var door = f.Door.Value;
        var spent = price.Committed.Select(p => (p.Walls * wall) + (p.Doors * door)).Order().ToList();
        var horizon = new ColonyModelPolicy().HorizonDays;
        var losses = f.Losses.Where(double.IsFinite).ToList();
        var spare = Math.Max(0, count - f.Losses.Count - f.Unpriced);
        List<double> costs = [.. Enumerable.Repeat(0.0, spare), .. losses];
        costs.Sort();
        var top = Math.Min(count, costs.Count);
        // The spend on the committed rooms a size gives up, least spent first: a barracks of k beds and count - k bedrooms
        // are count - (k - 1) rooms.
        double GivenUp(int k) => spent.Take(Math.Max(0, spent.Count - (count - (k - 1)))).Sum();
        var values = new double[Math.Max(top, 1) + 1];
        values[1] = -GivenUp(1);
        var best = 1;
        for (var k = 2; k <= top; k++)
        {
            if (Eval([new ClusterUnit(n, k)]).Area > MaxArea)
            {
                top = k - 1;
                break;
            }
            values[k] = ((k - 1) * room) - (costs.Take(k).Sum() * horizon) - GivenUp(k);
            if (values[k] > values[best])
            {
                best = k;
            }
        }
        var incumbent = Math.Max(1, price.Incumbent);
        if (incumbent <= top && best != incumbent && values[best] - values[incumbent] <= new ForecastValuePolicy().TieResolution)
        {
            best = incumbent;
        }
        return new BarracksChoice(best, "");
    }

    /// <summary>
    /// The capability-days one bedroom of area cells costs that a barracks bed saves (#3658): the ring of the squarest
    /// rectangle holding it less its door cell in the wall's price, the door, and the room's climate load with a closed
    /// door to the outdoors (the living band) over the horizon at the work-day's watts. Null, with the finding, when the
    /// wall or door price is unknown, or the heat is unknown on a cold or hot map; an unknown heat on a mild map is zero.
    /// </summary>
    private (double? Saved, string Finding) SavedPerRoom(int area, BedMergePrice price)
    {
        if (!price.Facts.WallCell.TryGetValue(out var wall) || !price.Facts.Door.TryGetValue(out var door))
        {
            return (null, LayoutClusterRules.ReasonClusterBedMergeUnpriced + ":walls");
        }
        var w = (int)Math.Ceiling(Math.Sqrt(area));
        var h = (int)Math.Ceiling(area / (double)w);
        var ring = (2 * (w + h)) + 4;
        var shell = ((ring - 1) * wall) + door;
        if (HeatPerDay(area, price) is { } heat)
        {
            return (shell + (heat * new ColonyModelPolicy().HorizonDays), "");
        }
        return price.Harsh ? (null, LayoutClusterRules.ReasonClusterBedMergeUnpriced + ":thermal") : (shell, "");
    }

    /// <summary>The capability-days a day of holding one bedroom of area cells in the living band behind a closed door to the outdoors; null while the curve, the climate model or the work-day is unknown.</summary>
    private double? HeatPerDay(int area, BedMergePrice price)
    {
        var m = _in.Catalog.Climate;
        if (!price.OutdoorCurve.TryGetValue(out var curve) || !m.HasLiving() || !QuestCapacityForecast.FinitePositive(price.WorkDayW))
        {
            return null;
        }
        try
        {
            var room = new ClimateRoom
            {
                Cells = area,
                Band = m.Living,
                Links = EquatableArray.From(new[] { new ClimateLink { Opening = Opening.DoorClosed, Side = new ClimateSide { Outdoors = true } } }),
            };
            return ClimateRules.ClimateLoad(m, curve, room, 0).MeanW / price.WorkDayW;
        }
        catch (DomainException)
        {
            return null;
        }
    }
}
