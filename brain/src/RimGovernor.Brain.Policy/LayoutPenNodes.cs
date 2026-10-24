namespace RimGovernor.Brain.Policy;

/// <summary>One race's herd: the planned animals of the race.</summary>
public sealed record StationHerd(Resource Race, int Animals)
{
    public StationHerd() : this(default, 0)
    {
    }
}

/// <summary>The pen of one race's herd.</summary>
public sealed record PenNode
{
    public string Id { get; init; } = "";
    /// <summary>The nutrition a day the herd eats (AdultFeedPerDay times the animals); 0 for a race that needs no food.</summary>
    public double Hunger { get; init; }

}

/// <summary>Pens as outdoor nodes of the station graph.</summary>
public static partial class LayoutPenNodesRules
{
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
            var node = new PenNode { Id = Resource.PenId(h.Race), Hunger = PenHunger(h, race) };
            foreach (var p in race.Products)
            {
                if (!HerdPlanRules.HerdProductJob(p).Item2)
                {
                    continue;
                }
                var amount = p.Amount;
                var interval = p.IntervalDays;
                if (amount <= 0 || interval <= 0)
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

    /// <summary>The herd's nutrition a day: the race's adult feed times the animals, 0 for a race that needs none; a feed that is no number fails the graph naming it.</summary>
    private static double PenHunger(StationHerd h, AnimalRace race)
    {
        if (race.AdultFeed is RaceFeed.NoNeed)
        {
            return 0;
        }
        if (race.AdultFeed is RaceFeed.Eats { PerDay: var feed } && FoodForecastRules.FoodNumber(feed))
        {
            return feed * h.Animals;
        }
        throw new RequiredFactException("adult feed of " + h.Race.Value);
    }

    /// <summary>Sizes the pen's hand feeding: the hunger less what PenCells' worth of the weakest grazing terrain grows, hauled in the race's richest feed item (the fewest items) from the store that holds it.</summary>
    private PenNode PenFeed(PenNode node, AnimalRace race)
    {
        // The pasture table is the food family's read to harden (#3786).
        var hunger = node.Hunger;
        if (!this.In.Pasture.TryGetValue(out var pasture))
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
