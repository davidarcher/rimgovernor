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

    /// <summary>
    /// A spot-now/table-later chain: <paramref name="First"/> serves the load from its own readiness until
    /// <paramref name="Then"/>, the permanent host, serves it (stands usable and reachable with an active bill of the load).
    /// Then's step depends on First's: binding never starts it before First stands. First's fate is a comparison of its own
    /// once Then serves: removed (<see cref="Retained"/> of Then) or kept as a second host (<see cref="Pair"/>).
    /// </summary>
    public sealed record Chain(string First, string Then) : HostChoice
    {
        public override string Bench => Then;
    }

    /// <summary>
    /// <paramref name="Def"/> hosts the load and the standing <paramref name="Also"/> is retained beside it: it takes copies of
    /// the load's dispatcher-sized orders (WorkRules.Stocked) up to its capacity once Def's is spent, so the two share one
    /// demand and never double it.
    /// </summary>
    public sealed record Pair(string Def, string Also) : HostChoice
    {
        public override string Bench => Def;
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
public sealed record HostedRegroupInput(HostAlternative Host, RegroupInput Input)
{
    /// <summary>The barracks size a bed-sharing alternative fixes (LayoutBedShareRules); a host alternative leaves it proposed.</summary>
    public BedShare Share { get; init; } = new BedShare.Proposed();

    /// <summary>The activity-site deviation an activity-site alternative is (ActivitySiteRules.Deviations); none for a host or bed-sharing one.</summary>
    public SiteDeviation Site { get; init; } = new SiteDeviation.None();

    /// <summary>The medical beds a medical bed alternative fixes (LayoutHospitalBedRules); null for every other alternative.</summary>
    public HospitalBedChoice? HospitalBeds { get; init; }
}

/// <summary>
/// The regroup inputs of every compared host alternative, the baseline first, and the deviations the bounds omitted: the
/// hosts past LayoutHostRules.HostTrials and each kind's site rooms past ActivitySiteRules.MaxRoomTrials (SitesOmitted).
/// </summary>
public sealed record HostedRegroupInputs(IReadOnlyList<HostedRegroupInput> Alternatives, EquatableArray<HostDeviation> Omitted)
{
    public EquatableArray<SiteTruncation> SitesOmitted { get; init; }

    /// <summary>The medical bed candidate defs past LayoutHospitalBedRules.MaxDefTrials, compared by nobody.</summary>
    public EquatableArray<string> MedicalBedsOmitted { get; init; }
}

/// <summary>
/// A standing bench as the host comparison reads it (RG-HABITAT-005): whether it serves (<see cref="Serves"/>: usable, as
/// native's bill giver reports it, and its interaction cell reachable from the colony), the recipes it offers now, and the ids of its bills a pawn's current job is working (<see cref="Working"/>).
/// </summary>
public sealed record HostBench(string Id, string Def, bool Serves, EquatableArray<string> Recipes, EquatableArray<string> Working);

/// <summary>
/// The planning census facts a new host is judged by (RG-HABITAT-005): each bench definition's availability
/// (<see cref="BenchDefinition.Available"/>) and each recipe's own research (<see cref="RecipeFlow.Research"/>) against
/// the finished projects, the facts the research rung reads (WorkRules.WorkshopResearchRung), and the standing benches
/// (<see cref="Benches"/>) a chain's replacement serves at.
/// </summary>
public sealed record HostCensus(IReadOnlyDictionary<string, BenchDefinition> Definitions, IReadOnlyDictionary<string, RecipeFlow> Recipes, IReadOnlySet<string> Finished)
{
    /// <summary>The standing benches (<see cref="LedgerView.Benches"/>).</summary>
    public IReadOnlyList<HostBench> Benches { get; init; } = [];

    /// <summary>
    /// Whether a new <paramref name="def"/> may host <paramref name="recipe"/> now: the definition is available and every
    /// project the recipe requires is finished. A definition or recipe the census does not describe throws
    /// <see cref="RequiredFactException"/> naming it; it is never taken as available or gated.
    /// </summary>
    public bool Buildable(string def, string recipe) =>
        (Definitions.TryGetValue(def, out var d) ? d.Available : throw new RequiredFactException("bench definition:" + def))
        && (Recipes.TryGetValue(recipe, out var r) ? r.Research.All(Finished.Contains) : throw new RequiredFactException("recipe flow:" + recipe));

    /// <summary>Whether a bench of <paramref name="def"/> stands.</summary>
    public bool Stands(string def) => Benches.Any(b => b.Def == def);

    /// <summary>
    /// Whether a standing <paramref name="def"/> serves <paramref name="recipe"/>: built, usable (powered, fuelled, unbroken),
    /// reachable, offering it. A placed blueprint or frame is no standing bench and never serves.
    /// </summary>
    public bool Serving(string def, string recipe) => LayoutHostRules.Serving(Benches, def, recipe);
}

/// <summary>
/// Generates the host alternatives of the ledger's recipe loads (RG-HABITAT-005). An unhosted load is an order naming no
/// bench kind, which no standing bench lists (GearOrdersRules.GearBenchDef); a kind-named load is a recipe whose orders all
/// name one catalog host, which stands. The baseline retains the held plan's host of each load (a chain past its bridge
/// once the replacement serves), else defers an unhosted load and keeps a kind-named one at its kind. Each deviation changes
/// one load: to another buildable catalog host or to deferral, to a spot-now/table-later chain (<see cref="HostChoice.Chain"/>),
/// or, once a host serves, to keeping or removing a standing spare (<see cref="HostChoice.Pair"/>). No population or bench
/// cap picks any of them. Deviations are taken round-robin over the loads (by recipe) and each load's choices (single hosts
/// by name, deferral, chains, then the spare's fate) up to the bound, which limits computation only and is reported, never
/// a capacity quota.
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

    /// <summary>Whether a standing bench of <paramref name="def"/> serves <paramref name="recipe"/>: usable, reachable, offering it.</summary>
    public static bool Serving(IEnumerable<HostBench> benches, string def, string recipe) => benches.Any(b => b.Def == def && b.Serves && b.Recipes.Contains(recipe));

    /// <summary>Whether <paramref name="def"/> serves <paramref name="recipe"/> in the ledger's Round: a standing bench of it serves it and an order of the recipe is declared.</summary>
    public static bool Serving(LedgerView view, string def, string recipe) => Serving(view.Benches, def, recipe) && view.Orders.Any(o => o.Recipe == recipe && o.Target > 0);

    /// <summary>Whether the catalog station <paramref name="def"/> hosts <paramref name="recipe"/>: a worker station listing its work.</summary>
    internal static bool Hosts(StationCatalog catalog, string def, string recipe) =>
        catalog.Station(def) is ({ Capacity.Kind: CapacityKind.Workers } s, true) && LayoutGraphRules.HostWork(s, recipe) > 0;

    /// <summary>
    /// The ledger's recipe loads by recipe: its orders with a target and no bench kind, else (none of those) its orders
    /// with a target when all name one kind the catalog hosts the recipe at, with that kind. Each has its catalog hosts by
    /// name and, of those, the ones <paramref name="census"/> lets the colony build now (<see cref="HostCensus.Buildable"/>).
    /// </summary>
    internal static List<(string Recipe, OrderSpec[] Orders, string Kind, string[] Hosts, string[] Buildable)> RecipeLoads(StationCatalog catalog, IReadOnlyList<OrderSpec> orders, HostCensus census) =>
    [
        .. orders.Where(o => o.Target > 0).GroupBy(o => o.Recipe, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var unhosted = g.Where(o => o.BenchKind.Length == 0).ToArray();
                var kinds = g.Select(o => o.BenchKind).Distinct(StringComparer.Ordinal).ToArray();
                return unhosted.Length != 0 ? (Recipe: g.Key, Orders: unhosted, Kind: "") : (Recipe: g.Key, Orders: kinds.Length == 1 ? g.ToArray() : [], Kind: kinds[0]);
            })
            .Where(l => l.Orders.Length != 0)
            .Select(l => (l.Recipe, l.Orders, l.Kind, Hosts: catalog.Defs().Where(def => Hosts(catalog, def, l.Recipe)).ToArray()))
            .Where(l => l.Hosts.Length != 0 && (l.Kind.Length == 0 || l.Hosts.Contains(l.Kind)))
            .Select(l => (l.Recipe, l.Orders, l.Kind, l.Hosts, l.Hosts.Where(def => census.Buildable(def, l.Recipe)).ToArray())),
    ];

    /// <summary>
    /// The held plan's host of <paramref name="recipe"/>: a Bridge and a Host production are its chain, a Host and a Share its
    /// pair, a Host alone its retained bench; with none, a kind-named load stays at its kind and an unhosted one defers. A
    /// chain whose replacement serves (<see cref="HostCensus.Serving"/>) is past its bridge: the baseline removes the bridge,
    /// and keeping it is a deviation of its own.
    /// </summary>
    internal static HostChoice Current(string recipe, string kind, string[] hosts, LayoutPlan held, HostCensus census)
    {
        var made = held.Rooms.SelectMany(r => r.Production).Where(p => p.Recipe == recipe && hosts.Contains(p.Bench)).ToArray();
        string? Step(HostStep step) => made.Where(p => p.Step == step).Select(p => p.Bench).Order(StringComparer.Ordinal).FirstOrDefault();
        var (host, bridge, share) = (Step(HostStep.Host), Step(HostStep.Bridge), Step(HostStep.Share));
        HostChoice current = host is null ? kind.Length != 0 ? new HostChoice.Retained(kind) : new HostChoice.Deferred()
            : bridge is not null && bridge != host ? new HostChoice.Chain(bridge, host)
            : share is not null && share != host ? new HostChoice.Pair(host, share)
            : new HostChoice.Retained(host);
        return current is HostChoice.Chain chain && census.Serving(chain.Then, recipe) ? new HostChoice.Retained(chain.Then) : current;
    }

    /// <summary>
    /// The deviations of one load from <paramref name="current"/>, in trial order: each other buildable single host and
    /// deferral of an unhosted load (a kind-named one runs at its kind already), each chain whose bridge stands or is
    /// buildable and whose replacement is buildable, the kind-named load's own kind, and the spare's fate once a host
    /// serves: each standing other host kept beside it, or the kept spare removed.
    /// </summary>
    internal static IEnumerable<HostChoice> Deviations(string recipe, string kind, string[] hosts, string[] buildable, HostChoice current, HostCensus census)
    {
        var options = new List<HostChoice>();
        if (kind.Length == 0)
        {
            options.AddRange(buildable.Where(d => d != current.Bench).Select(d => new HostChoice.Build(d)));
            if (current.Bench.Length != 0) options.Add(new HostChoice.Deferred());
            options.AddRange(hosts.Where(f => buildable.Contains(f) || census.Stands(f))
                .SelectMany(f => buildable.Where(t => t != f).Select(t => new HostChoice.Chain(f, t))));
        }
        else
        {
            options.AddRange(buildable.Where(t => t != kind).Select(t => new HostChoice.Chain(kind, t)));
            options.Add(new HostChoice.Retained(kind));
        }
        if (current is HostChoice.Pair pair) options.Add(new HostChoice.Retained(pair.Def));
        else if (current is HostChoice.Retained retained && census.Serving(retained.Def, recipe))
            options.AddRange(hosts.Where(s => s != retained.Def && census.Stands(s)).Select(s => new HostChoice.Pair(retained.Def, s)));
        return options.Where(o => o != current).Distinct();
    }

    /// <summary>
    /// The baseline and the bounded deviations over <paramref name="orders"/> with <paramref name="held"/>, the plan the
    /// colony holds. Only a host <paramref name="census"/> finds buildable is a <see cref="HostChoice.Build"/> or a chain's
    /// replacement; a held host is retained whatever its gate. An unhosted load with neither is no load of the comparison:
    /// only research stands between the colony and a host, which the research rung names (WorkRules.WorkshopResearchRung).
    /// A kind-named load with no deviation runs at its kind and is no load of the comparison either.
    /// </summary>
    public static HostAlternatives Of(StationCatalog catalog, IReadOnlyList<OrderSpec> orders, LayoutPlan held, HostCensus census)
    {
        var loads = RecipeLoads(catalog, orders, census).Select(l =>
        {
            var current = Current(l.Recipe, l.Kind, l.Hosts, held, census);
            var options = Deviations(l.Recipe, l.Kind, l.Hosts, l.Buildable, current, census).Select(h => new HostDeviation(l.Recipe, h)).ToArray();
            var compared = l.Kind.Length == 0 ? l.Buildable.Length != 0 || current is not HostChoice.Deferred : options.Length != 0;
            return (Load: new HostedLoad(l.Recipe, EquatableArray.From(l.Orders), current), Options: options, Compared: compared);
        }).Where(l => l.Compared).ToArray();
        var baseline = loads.Select(l => l.Load).ToArray();
        var ordered = new List<HostDeviation>();
        for (var round = 0; loads.Any(l => round < l.Options.Length); round++)
            foreach (var load in loads)
                if (round < load.Options.Length) ordered.Add(load.Options[round]);
        var alternatives = new List<HostAlternative> { new(BaselineId, EquatableArray.From(baseline)) };
        foreach (var deviation in ordered.Take(HostTrials))
        {
            var changed = baseline.Select(l => l.Recipe == deviation.Recipe ? l with { Host = deviation.Host } : l);
            alternatives.Add(new HostAlternative(IdOf(deviation), EquatableArray.From(changed)) { Change = deviation });
        }
        return new(EquatableArray.From(alternatives), EquatableArray.From(ordered.Skip(HostTrials)));
    }

    /// <summary>The alternative id of a deviation: <c>host:{recipe}/{name}</c>.</summary>
    public static string IdOf(HostDeviation deviation) => "host:" + deviation.Recipe + "/" + NameOf(deviation.Host);

    /// <summary>The host's name in a decision: its definition, <see cref="DeferredName"/>, <c>{first}&gt;{then}</c> for a chain or <c>{def}+{also}</c> for a pair.</summary>
    public static string NameOf(HostChoice host) =>
        host is HostChoice.Chain c ? c.First + ">" + c.Then
        : host is HostChoice.Pair p ? p.Def + "+" + p.Also
        : host.Bench.Length == 0 ? DeferredName : host.Bench;
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
