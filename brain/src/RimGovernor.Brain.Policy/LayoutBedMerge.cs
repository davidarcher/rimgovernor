namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the colony reads for the bed merge's price (#3658): each pool sleeper's capability-days lost a day sleeping in a
/// barracks over a bedroom (BedroomGain at the planned stage minus the barracks gain there), unknown while any sleeper's
/// loss is unread, and the capability-days one wall cell and one door of a planned bedroom's base ring cost to build
/// (labor plus material) in the stuff the ring's sites are placed in. Each is a required planner input (#3747): the
/// chain fails naming the one unread.
/// </summary>
public sealed record BedMergeFacts
{
    public Fact<EquatableArray<double>> Losses { get; init; }

    public Fact<double> WallCell { get; init; }

    public Fact<double> Door { get; init; }
}

/// <summary>Everything the clustering prices a bed merge with (#3658): the colony's facts, the climate the saved room's heat is read in, and the barracks the plan holds.</summary>
public sealed record BedMergePrice
{
    public BedMergeFacts Facts { get; init; } = new();

    public Fact<EquatableArray<double>> OutdoorCurve { get; init; }

    /// <summary>The watts a colonist's work-day is worth (WorkPrice.WorkDayW); unknown while the work price is (its zero).</summary>
    public Fact<double> WorkDayW { get; init; }

    /// <summary>The bed slots of the barracks the plan holds (BedSlotsOf), 0 for none: the incumbent a new size must beat by more than the tie resolution.</summary>
    public int Incumbent { get; init; }

    /// <summary>
    /// The plan's committed barracks and bedrooms, each with what of its shell stands (#3661, #3675): a room standing or
    /// under construction. A size that leaves fewer rooms than these gives up the least spent of them and pays what was
    /// spent on each, so a split being built is not merged back by the next review; a room on paper is free to drop.
    /// </summary>
    public EquatableArray<ShellPieces> Committed { get; init; }
}

public static partial class LayoutClusterRules
{
    /// <summary>The free-bed pool's size was priced (#3658): the decision's verdict is merge for a barracks of more than one bed, split otherwise.</summary>
    public const string ReasonClusterBedMerge = "cluster_bed_merge_priced";

    /// <summary>
    /// The barracks size the input's free-bed pool takes (#3658, Clusterer.BarracksSize): null when the graph has no free
    /// pool or the input prices no merge. A required fact unread fails it with a StationFactsException (#3747).
    /// </summary>
    public static int? BarracksSizeOf(ClusterInput input)
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
        var size = BarracksSize(n, count, price);
        decisions.Add(new ClusterDecision
        {
            A = node.Id,
            Verdict = size > 1 ? LayoutClusterRules.ClusterMerge : LayoutClusterRules.ClusterSplit,
            Reason = LayoutClusterRules.ReasonClusterBedMerge,
        });
        var output = new List<ClusterUnit> { new(n, size) };
        for (var i = size; i < count; i++)
        {
            output.Add(new ClusterUnit(n, 1));
        }
        return output;
    }

    /// <summary>
    /// The barracks size of a pool of count free beds of node n (#3658): the k that maximises (k-1) times the price of one
    /// bedroom saved less the k cheapest sleepers' losses over the horizon (ColonyModelPolicy.HorizonDays), its spare beds
    /// losing nothing. A size whose room breaks the area limit is not taken. The incumbent (the plan's barracks, else every
    /// bed single) holds unless the best beats it by more than ForecastValuePolicy.TieResolution. The sleepers' losses,
    /// the shell's price and the room's heat are required (#3747): one unread throws a StationFactsException naming it, so
    /// the chain's run waits for it.
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
    internal int BarracksSize(int n, int count, BedMergePrice price)
    {
        var f = price.Facts;
        if (!f.Losses.TryGetValue(out var read))
        {
            throw LayoutStationPlanRules.UnknownStationFact("sleeper_mood");
        }
        var single = Eval([new ClusterUnit(n, 1)]);
        var (wall, door, room) = SavedPerRoom(single.Area, price);
        var spent = price.Committed.Select(p => (p.Walls * wall) + (p.Doors * door)).Order().ToList();
        var horizon = new ColonyModelPolicy().HorizonDays;
        var losses = read.Where(double.IsFinite).ToList();
        var spare = Math.Max(0, count - read.Count);
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
        return best;
    }

    /// <summary>
    /// The wall cell's and door's prices and the capability-days one bedroom of area cells costs that a barracks bed saves
    /// (#3658): the ring of the squarest rectangle holding it less its door cell in the wall's price, the door, and the
    /// room's climate load with a closed door to the outdoors (the living band) over the horizon at the work-day's watts.
    /// An unread wall or door price throws a StationFactsException naming it.
    /// </summary>
    private (double Wall, double Door, double Saved) SavedPerRoom(int area, BedMergePrice price)
    {
        if (!price.Facts.WallCell.TryGetValue(out var wall))
        {
            throw LayoutStationPlanRules.UnknownStationFact("wall_price");
        }
        if (!price.Facts.Door.TryGetValue(out var door))
        {
            throw LayoutStationPlanRules.UnknownStationFact("door_price");
        }
        var w = (int)Math.Ceiling(Math.Sqrt(area));
        var h = (int)Math.Ceiling(area / (double)w);
        var ring = (2 * (w + h)) + 4;
        var shell = ((ring - 1) * wall) + door;
        return (wall, door, shell + (HeatPerDay(area, price) * new ColonyModelPolicy().HorizonDays));
    }

    /// <summary>
    /// The capability-days a day of holding one bedroom of area cells in the living band behind a closed door to the
    /// outdoors. Every input is required on every map (#3747): an unread outdoor curve, work-day or living band, or a load
    /// the climate model cannot give, throws a StationFactsException naming it.
    /// </summary>
    private double HeatPerDay(int area, BedMergePrice price)
    {
        var m = _in.Catalog.Climate;
        if (!price.OutdoorCurve.TryGetValue(out var curve))
        {
            throw LayoutStationPlanRules.UnknownStationFact("outdoor_curve");
        }
        if (!price.WorkDayW.TryGetValue(out var workDay))
        {
            throw LayoutStationPlanRules.UnknownStationFact("work_day");
        }
        if (!m.HasLiving())
        {
            throw LayoutStationPlanRules.UnknownStationFact("living_band");
        }
        try
        {
            var room = new ClimateRoom
            {
                Cells = area,
                Band = m.Living,
                Links = EquatableArray.From(new[] { new ClimateLink { Opening = Opening.DoorClosed, Side = new ClimateSide { Outdoors = true } } }),
            };
            return ClimateRules.ClimateLoad(m, curve, room, 0).MeanW / workDay;
        }
        catch (DomainException e)
        {
            throw LayoutStationPlanRules.UnknownStationFact("bedroom_heat: " + e.Message);
        }
    }
}
