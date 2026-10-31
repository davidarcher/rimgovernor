namespace RimGovernor.Brain.Policy;

/// <summary>
/// The bench that hosts one unhosted recipe load in a layout alternative (RG-HABITAT-005). No definition is chosen before
/// the shared layout comparison: each choice is a station graph of its own, tiled, read natively and priced by the
/// canonical ledger (<see cref="LayoutRegroupRules.RegroupAsync(HostedRegroupInputs, Func{IReadOnlyList{RegroupCandidate}, CancellationToken, Task{IReadOnlyList{RegroupValuation}}}, CancellationToken)"/>).
/// </summary>
public abstract record HostChoice
{
    /// <summary>The bench definition the choice plans, or empty when it defers.</summary>
    public abstract string Bench { get; }

    /// <summary>The bench the held plan already hosts the load at (<see cref="PlannedRoom.Production"/>): reuse of its standing, building or still owed piece.</summary>
    public sealed record Retained(string Def) : HostChoice
    {
        public override string Bench => Def;
    }

    /// <summary>A new bench of a catalog definition that hosts the recipe.</summary>
    public sealed record Build(string Def) : HostChoice
    {
        public override string Bench => Def;
    }

    /// <summary>No planned bench hosts the load: the current provision stands, and the load earns no credit.</summary>
    public sealed record Deferred : HostChoice
    {
        public override string Bench => "";
    }
}

/// <summary>
/// One unhosted recipe load: the recipe (its function), the ledger's orders it serves, whose declaring owners
/// (<see cref="OrderSpec.Owner"/>) are its beneficiaries, and its host in this alternative. Placement, prerequisites and
/// readiness belong to the tiled trial and are joined there, not copied: the host's piece and room
/// (<see cref="PlannedRoom.Production"/>, <see cref="LayoutPlan.Pieces"/>), its planned build site, the room's prerequisite
/// sites (<see cref="ForecastCoupledContext.RoomPrerequisites"/>) and its service day (<see cref="ForecastRoomService.ServedAt"/>).
/// </summary>
public sealed record HostedLoad(string Recipe, EquatableArray<OrderSpec> Orders, HostChoice Host);

/// <summary>A change of one load's host against the baseline alternative.</summary>
public sealed record HostDeviation(string Recipe, HostChoice Host);

/// <summary>One complete host assignment of the station graph. Change is null for the baseline.</summary>
public sealed record HostAlternative(string Id, EquatableArray<HostedLoad> Loads)
{
    public HostDeviation? Change { get; init; }
}

/// <summary>The baseline first, then the deviations within <see cref="LayoutHostRules.HostTrials"/>; Omitted names those past the bound.</summary>
public sealed record HostAlternatives(EquatableArray<HostAlternative> Alternatives, EquatableArray<HostDeviation> Omitted)
{
    public HostAlternative Baseline => Alternatives[0];
}

/// <summary>One host alternative with the regroup input over the station graph built for it.</summary>
public sealed record HostedRegroupInput(HostAlternative Host, RegroupInput Input);

/// <summary>The regroup inputs of every compared host alternative, the baseline first, and the deviations the bound omitted.</summary>
public sealed record HostedRegroupInputs(IReadOnlyList<HostedRegroupInput> Alternatives, EquatableArray<HostDeviation> Omitted);

/// <summary>
/// Generates the host alternatives of the ledger's unhosted loads (RG-HABITAT-005): an order naming no bench kind, which
/// no standing bench lists (GearOrdersRules.GearBenchDef). An order naming a kind keeps it: its bills run on that kind. The baseline retains the held plan's host of each load, else
/// defers it; each deviation changes one load to another catalog host or to deferral. Deviations are taken round-robin
/// over the loads (by recipe) and each load's hosts (by name, deferral last) up to the bound, which limits computation
/// only and is reported, never a capacity quota.
/// </summary>
public static class LayoutHostRules
{
    /// <summary>The deviations compared beside the baseline, as the regroup search's own proposals a round.</summary>
    public const int HostTrials = LayoutSiteGroundRules.SiteSearchTop;

    public const string BaselineId = "host:baseline";

    /// <summary>The regroup decision kind of a host deviation.</summary>
    public const string HostMove = "host";

    /// <summary>The decision reason naming a deviation the bound left out.</summary>
    public const string HostTruncated = "regroup_host_alternatives_truncated";

    /// <summary>The decision's host name for a deferral.</summary>
    public const string DeferredName = "deferred";

    /// <summary>Whether the catalog station <paramref name="def"/> hosts <paramref name="recipe"/>: a worker station listing its work.</summary>
    internal static bool Hosts(StationCatalog catalog, string def, string recipe) =>
        catalog.Station(def) is ({ Capacity.Kind: CapacityKind.Workers } s, true) && LayoutGraphRules.HostWork(s, recipe) > 0;

    /// <summary>The ledger's orders with a target and no bench kind, by recipe, each with its catalog hosts by name.</summary>
    internal static List<(string Recipe, OrderSpec[] Orders, string[] Hosts)> UnhostedLoads(StationCatalog catalog, IReadOnlyList<OrderSpec> orders) =>
    [
        .. orders.Where(o => o.Target > 0 && o.BenchKind.Length == 0).GroupBy(o => o.Recipe, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.ToArray(), catalog.Defs().Where(def => Hosts(catalog, def, g.Key)).ToArray()))
            .Where(l => l.Item3.Length != 0),
    ];

    /// <summary>The baseline and the bounded deviations over <paramref name="orders"/> with <paramref name="held"/>, the plan the colony holds.</summary>
    public static HostAlternatives Of(StationCatalog catalog, IReadOnlyList<OrderSpec> orders, LayoutPlan held)
    {
        var loads = UnhostedLoads(catalog, orders);
        var baseline = loads.Select(l =>
        {
            var retained = held.Rooms.SelectMany(r => r.Production).Where(p => p.Recipe == l.Recipe && l.Hosts.Contains(p.Bench))
                .Select(p => p.Bench).Order(StringComparer.Ordinal).FirstOrDefault();
            return new HostedLoad(l.Recipe, EquatableArray.From(l.Orders), retained is null ? new HostChoice.Deferred() : new HostChoice.Retained(retained));
        }).ToArray();
        var options = loads.Select((l, i) =>
        {
            var current = baseline[i].Host.Bench;
            IEnumerable<HostChoice> hosts = l.Hosts.Where(d => d != current).Select(d => (HostChoice)new HostChoice.Build(d));
            if (current.Length != 0) hosts = hosts.Append(new HostChoice.Deferred());
            return hosts.Select(h => new HostDeviation(l.Recipe, h)).ToArray();
        }).ToArray();
        var ordered = new List<HostDeviation>();
        for (var round = 0; options.Any(o => round < o.Length); round++)
            foreach (var option in options)
                if (round < option.Length) ordered.Add(option[round]);
        var alternatives = new List<HostAlternative> { new(BaselineId, EquatableArray.From(baseline)) };
        foreach (var deviation in ordered.Take(HostTrials))
        {
            var changed = baseline.Select(l => l.Recipe == deviation.Recipe ? l with { Host = deviation.Host } : l);
            alternatives.Add(new HostAlternative(IdOf(deviation), EquatableArray.From(changed)) { Change = deviation });
        }
        return new(EquatableArray.From(alternatives), EquatableArray.From(ordered.Skip(HostTrials)));
    }

    /// <summary>The alternative id of a deviation: <c>host:{recipe}/{def or deferred}</c>.</summary>
    public static string IdOf(HostDeviation deviation) => "host:" + deviation.Recipe + "/" + NameOf(deviation.Host);

    /// <summary>The host's name in a decision: its definition, or <see cref="DeferredName"/>.</summary>
    public static string NameOf(HostChoice host) => host.Bench.Length == 0 ? DeferredName : host.Bench;
}

/// <summary>
/// What the held plan binds one of an owner's unhosted recipe loads to (RG-HABITAT-005, RG-HABITAT-011): the adopted host
/// alternative is the plan's own <see cref="PlannedRoom.Production"/> and pieces, so binding reads it and never selects a
/// bench again (<see cref="PlannedRules.HostBindingOf"/>). Only a <see cref="Build"/> becomes a construction Method;
/// none of them satisfies the owner's Need, whose own predicate reads usable service on fresh facts.
/// </summary>
public abstract record HostBinding(string Recipe)
{
    /// <summary>
    /// The plan's next unbuilt host piece in an open room: built at its anchor and rotation in the stuff the comparison
    /// selected for its site (else the piece's own), and claimed by its footprint at admission.
    /// </summary>
    public sealed record Build(string Recipe, PlannedRoom Room, WantedPiece Piece) : HostBinding(Recipe);

    /// <summary>
    /// Every host piece of the room stands built (<paramref name="Built"/>) or as a blueprint or frame: the retained host
    /// binds nothing new, and a site is the open construction Method's, never a new one.
    /// </summary>
    public sealed record Standing(string Recipe, bool Built) : HostBinding(Recipe);

    /// <summary>
    /// The plan hosts the load in a room the portfolio has not opened (an unstarted ringed room the record does not select
    /// the host piece of, or one the record never assessed), or in a room the tiler left without a piece of the bench.
    /// </summary>
    public sealed record Held(string Recipe, int Room) : HostBinding(Recipe);

    /// <summary>No room of the plan hosts the load: the comparison deferred it, and no bench is bound for it.</summary>
    public sealed record Deferred(string Recipe) : HostBinding(Recipe);
}
