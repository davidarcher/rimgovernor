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
    /// <summary>The nutrition a day the herd eats (AdultFeedPerDay times the animals); unknown with the race's feed.</summary>
    public Fact<double> Hunger { get; init; }

}

/// <summary>Ports <c>layout_pen_nodes.go</c>: pens as outdoor nodes of the station graph.</summary>
public static partial class LayoutPenNodesRules
{
    /// <summary>Why the graph holds a pen: the herd the plan keeps of a race.</summary>
    public const string ReasonPenHerd = "pen_herd_of_race";

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
                if (HerdPlanRules.HerdProductJob(p).Item2)
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
}

partial record struct Resource
{

    internal static string PenId(Resource race) => "pen/" + race.Value;
}

partial class StationGraph
{
    /// <summary>The pen node with the id; null when absent.</summary>
    public PenNode? Pen(string id) => this.Pens.FirstOrDefault(p => p.Id == id);

    /// <summary>Whether a node is outside the base: the outside node or a pen.</summary>
    internal bool Outdoor(string id) => id == LayoutGraphRules.OutsideNode || this.Pen(id) is not null;
}

partial class GraphBuilder
{
    /// <summary>Adds a node for each race of the pen herd the catalog knows and the haul edges of its products and feed.</summary>
    internal List<PenNode> PenNodes()
    {
        var output = new List<PenNode>();
        foreach (var h in this.In.Population.Herds)
        {
            var ok = this.In.Races.Race(h.Race, out var race);
            if (!ok || h.Animals <= 0)
            {
                continue;
            }
            var node = new PenNode { Id = Resource.PenId(h.Race), Hunger = Unknown<double>() };
            if (race.AdultFeedPerDay.TryGetValue(out var feed) && FoodForecastRules.FoodNumber(feed))
            {
                node = node with { Hunger = Known(feed * h.Animals) };
            }
            foreach (var p in race.Products)
            {
                if (!HerdPlanRules.HerdProductJob(p).Item2)
                {
                    continue;
                }
                if (!p.Amount.TryGetValue(out var amount) || !p.IntervalDays.TryGetValue(out var interval) || amount <= 0 || interval <= 0)
                {
                    continue;
                }
                var (store, found) = this.StoreFor(p.Def);
                if (found)
                {
                    this.AddEdge(node.Id, store, h.Animals * amount / interval, false);
                }
            }
            node = this.PenFeed(node, race);
            output.Add(node);
        }
        return [.. output.OrderBy(p => p.Id, StringComparer.Ordinal)];
    }

    /// <summary>Sizes the pen's hand feeding: the hunger less what PenCells' worth of the weakest grazing terrain grows, hauled in the race's richest feed item (the fewest items) from the store that holds it.</summary>
    private PenNode PenFeed(PenNode node, AnimalRace race)
    {
        if (!node.Hunger.TryGetValue(out var hunger) || !this.In.Pasture.TryGetValue(out var pasture))
        {
            return node;
        }
        var (perCell, _) = pasture.Weakest();
        var shortfall = Math.Max(hunger - (PenFoodRules.PenCells(hunger, perCell) * perCell), 0);
        if (shortfall <= 0)
        {
            return node;
        }
        var feed = new RaceFeedItem(default, 0);
        foreach (var f in race.FeedItems)
        {
            if (FoodForecastRules.FoodNumber(f.Nutrition) && f.Nutrition > feed.Nutrition)
            {
                // FeedItems are in name order: the first of equals
                feed = f;
            }
        }
        if (feed.Nutrition <= 0)
        {
            return node;
        }
        var (store, found) = this.StoreFor(feed.Def);
        if (found)
        {
            this.AddEdge(node.Id, store, shortfall / feed.Nutrition, false);
        }
        return node;
    }
}
