namespace RimGovernor.Brain.Policy;

/// <summary>One race's herd: the planned animals of the race.</summary>
public sealed record StationHerd(Resource Race, int Animals)
{
    /// <summary>The zero value Go's struct literal starts from.</summary>
    public StationHerd() : this(default, 0)
    {
    }
}

/// <summary>The pen of one race's herd.</summary>
public sealed record PenNode
{
    public string Id { get; init; } = "";
    public Resource Race { get; init; }
    public int Animals { get; init; }

    /// <summary>The nutrition a day the herd eats (AdultFeedPerDay times the animals); unknown with the race's feed.</summary>
    public Fact<double> Hunger { get; init; }

    /// <summary>The nutrition a day the pen's grazing does not grow, hauled to the pen; unknown while the hunger or the pasture is.</summary>
    public Fact<double> HandFed { get; init; }

    public string Reason { get; init; } = "";
}

/// <summary>Ports <c>layout_pen_nodes.go</c>: pens as outdoor nodes of the station graph.</summary>
public static partial class PolicyFunctions
{
    /// <summary>Why the graph holds a pen: the herd the plan keeps of a race.</summary>
    public const string ReasonPenHerd = "pen_herd_of_race";

    public const string ReasonFlowPenProduct = "flow_pen_product";
    public const string ReasonFlowPenFeed = "flow_pen_feed";

    internal static string PenId(Resource race) => "pen/" + race.Value;

    /// <summary>The pen node with the id; null when absent.</summary>
    public static PenNode? Pen(this StationGraph g, string id) => g.Pens.FirstOrDefault(p => p.Id == id);

    /// <summary>Whether a node is outside the base: the outside node or a pen.</summary>
    internal static bool Outdoor(this StationGraph g, string id) => id == OutsideNode || g.Pen(id) is not null;

    /// <summary>The items the herds' pens move: their products and feed items. The caller reads their rot and stacks beside the recipes' items.</summary>
    public static List<Resource> PenItems(IEnumerable<StationHerd> herds, AnimalRaceCatalog races)
    {
        var output = new List<Resource>();
        foreach (var h in herds)
        {
            var ok = races.Race(h.Race, out var race);
            if (!ok || h.Animals <= 0)
            {
                continue;
            }
            foreach (var p in race.Products)
            {
                if (HerdProductJob(p).Item2)
                {
                    output.Add(p.Def);
                }
            }
            foreach (var f in race.FeedItems)
            {
                output.Add(f.Def);
            }
        }
        return [.. output.Select(r => r.Value).Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Select(v => new Resource(v))];
    }

    /// <summary>Adds a node for each race of the pen herd the catalog knows and the haul edges of its products and feed.</summary>
    internal static List<PenNode> PenNodes(this GraphBuilder b)
    {
        var output = new List<PenNode>();
        foreach (var h in b.In.Population.Herds)
        {
            var ok = b.In.Races.Race(h.Race, out var race);
            if (!ok || h.Animals <= 0)
            {
                continue;
            }
            var node = new PenNode { Id = PenId(h.Race), Race = h.Race, Animals = h.Animals, Hunger = Unknown<double>(), HandFed = Unknown<double>(), Reason = ReasonPenHerd };
            if (race.AdultFeedPerDay.TryGetValue(out var feed) && FoodNumber(feed))
            {
                node = node with { Hunger = Known(feed * h.Animals) };
            }
            foreach (var p in race.Products)
            {
                if (!HerdProductJob(p).Item2)
                {
                    continue;
                }
                if (!p.Amount.TryGetValue(out var amount) || !p.IntervalDays.TryGetValue(out var interval) || amount <= 0 || interval <= 0)
                {
                    continue;
                }
                var (store, found) = b.StoreFor(p.Def);
                if (found)
                {
                    b.AddEdge(node.Id, store, h.Animals * amount / interval, false, ReasonFlowPenProduct);
                }
            }
            node = b.PenFeed(node, race);
            output.Add(node);
        }
        return [.. output.OrderBy(p => p.Id, StringComparer.Ordinal)];
    }

    /// <summary>Sizes the pen's hand feeding: the hunger less what PenCells' worth of the weakest grazing terrain grows, hauled in the race's richest feed item (the fewest items) from the store that holds it.</summary>
    private static PenNode PenFeed(this GraphBuilder b, PenNode node, AnimalRace race)
    {
        if (!node.Hunger.TryGetValue(out var hunger) || !b.In.Pasture.TryGetValue(out var pasture))
        {
            return node;
        }
        var (perCell, _) = pasture.Weakest();
        var shortfall = Math.Max(hunger - (PenCells(hunger, perCell) * perCell), 0);
        node = node with { HandFed = Known(shortfall) };
        if (shortfall <= 0)
        {
            return node;
        }
        var feed = new RaceFeedItem(default, 0);
        foreach (var f in race.FeedItems)
        {
            if (FoodNumber(f.Nutrition) && f.Nutrition > feed.Nutrition)
            {
                // FeedItems are in name order: the first of equals
                feed = f;
            }
        }
        if (feed.Nutrition <= 0)
        {
            return node;
        }
        var (store, found) = b.StoreFor(feed.Def);
        if (found)
        {
            b.AddEdge(node.Id, store, shortfall / feed.Nutrition, false, ReasonFlowPenFeed);
        }
        return node;
    }
}
