using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Policy;

public sealed record PowerSite : PowerBuilding
{
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public IReadOnlyList<Cell> Occupied { get; init; } = [];
}

/// <summary>One steam geyser as the census reports it.</summary>
public sealed record PowerGeyser
{
    public string Id { get; init; } = "";

    public Cell Cell { get; init; }

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public bool Occupied { get; init; }
}

public sealed record PowerTopology
{
    public IReadOnlyList<PowerSite> Buildings { get; init; } = [];

    public IReadOnlyList<Cell> Conduits { get; init; } = [];

    public IReadOnlyList<Cell> UnsafeConduits { get; init; } = [];

    public Fact<bool> Blackout { get; init; }

    /// <summary>The native eclipse condition: solar output is zero for the rest of it, so the day's budget carries no solar energy.</summary>
    public Fact<bool> Eclipse { get; init; }

    /// <summary>The per-net energy summary; an empty census means the native read predates network facts and reserve runway stays unknown.</summary>
    public IReadOnlyList<PowerNetworkFact> Networks { get; init; } = [];

    /// <summary>
    /// The native steam-geyser census: every geyser on the map with its footprint and whether a harvester or
    /// another building already stands on it. A free geyser in reach hosts a geothermal generator.
    /// </summary>
    public IReadOnlyList<PowerGeyser> Geysers { get; init; } = [];
}

public enum PowerMethod
{
    [WireName("")]
    None = 0,
    [WireName("unknown")]
    Unknown,
    [WireName("no_deficit")]
    NoMethod,
    [WireName("waiting_for_native_power")]
    WaitOutput,
    [WireName("waiting_for_refuel")]
    WaitFuel,
    [WireName("waiting_for_repair")]
    WaitRepair,
    [WireName("solar_flare")]
    WaitBlackout,
    [WireName("player_disabled_power")]
    WaitPlayer,
    [WireName("no_observed_route")]
    RouteBlocked,
    [WireName("no_affordable_generator")]
    NoGenerator,
    [WireName("HiddenConduit")]
    Connect,
    [WireName("generate")]
    Generate,
    [WireName("shelter_power")]
    Shelter,
    [WireName("waiting_for_charge")]
    WaitCharge,
    [WireName("store")]
    Store,
}

/// <summary>
/// One generator definition the planner may compile, in preference order. Fuel names the resource a
/// refuelable generator burns and FuelStock the observed colony stock of it; a fuel-free generator leaves both
/// empty. Availability comes from native planning definitions (research and content), never from an
/// assumption about the installed game.
/// </summary>
public sealed record GeneratorOption
{
    public string Definition { get; init; } = "";

    public Fact<bool> Available { get; init; }

    public Resource Fuel { get; init; }

    public Fact<long> FuelStock { get; init; }

    public long MinimumFuel { get; init; }

    /// <summary>Whether the colony can grow its fuel here (a sowable crop harvests it); false is a finite stock that runs out.</summary>
    public bool FuelRenewable { get; init; }

    /// <summary>How the generator delivers its nominal watts over a day (DefinitionCatalog.PowerSources).</summary>
    public PowerSourceProfile Profile { get; init; }

    /// <summary>The producer's catalog wattage.</summary>
    public double NominalW { get; init; }

    /// <summary>The generator's build cost.</summary>
    public IReadOnlyList<Amount> Costs { get; init; } = [];
}

/// <summary>
/// The catalog's description of one generator definition: nominal watts, build cost and, for a refuelable
/// one, the fuel it burns and the tank a full load takes (the stock floor below which the planner will not
/// commit to that fuel).
/// </summary>
public sealed record GeneratorRow
{
    public double NominalW { get; init; }

    public IReadOnlyList<Amount> Costs { get; init; } = [];

    public Resource Fuel { get; init; }

    public long FuelCapacity { get; init; }
}

public static partial class PolicyConstants
{
    /// <summary>
    /// Every generator definition RankGenerators may choose, in the order that breaks ties among equal tiers
    /// and yields. Planners request these planning definitions and Hands correlate completed work against
    /// them. The wind turbine follows the solar generator: both are fuel-free, but a turbine needs an
    /// unobstructed catch zone the placement preview reports, so a site that has none falls back to the next
    /// ranked definition.
    /// </summary>
    public static readonly IReadOnlyList<string> GeneratorDefinitions = ["SolarGenerator", WindTurbineDefinition, "WoodFiredGenerator", "ChemfuelPoweredGenerator"];

    /// <summary>The ranked generator whose placement preview must report a clear catch zone.</summary>
    public const string WindTurbineDefinition = "WindTurbine";

    /// <summary>
    /// The generator a free steam geyser hosts. It is not ranked: SelectPowerMethod proposes it ahead of every
    /// other generator when a free geyser is in reach of the draining consumer.
    /// </summary>
    public const string GeothermalDefinition = "GeothermalGenerator";

    /// <summary>Bounds the conduit route from a draining consumer to a geyser the family will build on: longer routes are left to a nearer generator.</summary>
    public const int GeothermalReachCells = 48;
}

/// <summary>
/// The planner-side choices SelectPowerMethod needs beyond the observed topology: which generators are
/// compilable, whether a battery is (and its storage, from the catalog), how many days of stored reserve a
/// draining network must keep before the budget is consulted even though every consumer is currently powered,
/// and the margin the storage target carries over the night deficit.
/// </summary>
public sealed record PowerPlanning
{
    public IReadOnlyList<GeneratorOption> Generators { get; init; } = [];

    public Fact<bool> BatteryAvailable { get; init; }

    public PowerBattery Battery { get; init; }

    public Fact<bool> GeothermalAvailable { get; init; }

    /// <summary>The delivery profile of every producer definition the catalog has (DefinitionCatalog.PowerSources); a producer on the map that it lacks is an error.</summary>
    public IReadOnlyDictionary<string, PowerSourceProfile> Sources { get; init; } = new Dictionary<string, PowerSourceProfile>();

    /// <summary>The steel one component costs by the game's recipe; it prices generators' components for RankGenerators.</summary>
    public Fact<double> ComponentSteel { get; init; }

    public double ReserveMinDays { get; init; }

    public double StorageMargin { get; init; }

    /// <summary>
    /// The wattage of consumers other goals' admitted plans are about to build, counted in the budget's demand
    /// so generation and storage are sized for the colony being built, not only the one standing.
    /// </summary>
    public double PendingDemandW { get; init; }

    /// <summary>The terrain defs that afford Light: where the power route and a shelter may run (SiteCell.SupportsLight).</summary>
    public LightTerrains Light { get; init; } = new();

    /// <summary>
    /// The conduit net the layout plan laid (PlannedNetOf): a connect proposal runs along it, skipping the
    /// built cells, and searches the ground only where the plan holds no path.
    /// </summary>
    public PlannedNet Net { get; init; } = new();
}

/// <summary>
/// What RankGenerators knows about the colony beyond the options: whether a battery can be built (a renewable
/// is free to run but needs storage to carry the night), whether the shortfall being closed is night-only,
/// which a solar generator cannot touch, and the steel a component costs to make (the game's own recipe,
/// DefinitionCatalog.ProductInputCost), which prices a generator's components in steel. Unknown, no generator
/// can be priced and list order breaks ties.
/// </summary>
public sealed record GeneratorRanking
{
    public Fact<bool> BatteryAvailable { get; init; }

    public bool NightOnly { get; init; }

    public Fact<double> ComponentSteel { get; init; }
}

public sealed record PowerProposal
{
    public PowerMethod Method { get; init; }

    public string Definition { get; init; } = "";

    public MethodId Key { get; init; }

    public string Target { get; init; } = "";

    public Cell Center { get; init; }

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public Rectangle Room { get; init; }

    /// <summary>
    /// The generators ranked after Definition for a generate method, so a definition no site accepts (a wind
    /// turbine with every catch zone obstructed) yields to the next one under the same key.
    /// </summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    /// <summary>The target network's 24 h balance behind a generate, store or charge decision; zero for the connect and hold methods.</summary>
    public PowerBudget Budget { get; init; } = new();

    /// <summary>
    /// Reports whether the proposal names its exact placement: a geothermal generator is anchored on its
    /// geyser (Center) rather than searched for near the consumer.
    /// </summary>
    public bool FixedSite() => Method == PowerMethod.Generate && Definition == PolicyConstants.GeothermalDefinition;
}

public static partial class PolicyFunctions
{
    /// <summary>Every definition the power family compiles: the conduit, each generator, the geothermal generator and the battery.</summary>
    public static IReadOnlyList<string> PowerFamilyDefinitions() =>
        [Wire.Name(PowerMethod.Connect), .. PolicyConstants.GeneratorDefinitions, PolicyConstants.GeothermalDefinition, PolicyConstants.BatteryDefinition];

    public static PowerPlanning DefaultPowerPlanning() => new() { ReserveMinDays = 1, StorageMargin = 0.25 };

    /// <summary>
    /// Pairs each generator definition with its catalog row (cost, nominal watts, fuel and tank), the observed
    /// stock of its fuel, whether renewable says the fuel can be grown here, and its delivery profile from
    /// sources; a generator with no profile or row is an error (DomainException).
    /// </summary>
    public static IReadOnlyList<GeneratorOption> DefaultGeneratorOptions(
        Func<string, Fact<bool>> available,
        Func<Resource, Fact<long>> stock,
        Func<Resource, bool> renewable,
        IReadOnlyDictionary<string, PowerSourceProfile> sources,
        IReadOnlyDictionary<string, GeneratorRow> rows)
    {
        var options = new List<GeneratorOption>(PolicyConstants.GeneratorDefinitions.Count);
        foreach (var name in PolicyConstants.GeneratorDefinitions)
        {
            if (!sources.TryGetValue(name, out var profile))
            {
                throw new DomainException($"generator {name} has no catalog power source profile");
            }
            if (!rows.TryGetValue(name, out var row))
            {
                throw new DomainException($"generator {name} has no catalog row");
            }
            var option = new GeneratorOption { Definition = name, Available = available(name), Profile = profile, NominalW = row.NominalW, Costs = row.Costs };
            if (!row.Fuel.IsEmpty)
            {
                option = option with { Fuel = row.Fuel, MinimumFuel = row.FuelCapacity, FuelStock = stock(row.Fuel), FuelRenewable = renewable(row.Fuel) };
            }
            options.Add(option);
        }
        return options;
    }

    /// <summary>
    /// The average watts a generator delivers per steel of build cost, components counted at componentSteel
    /// steel each; zero when a cost cannot be priced (unknown component recipe or another resource).
    /// </summary>
    internal static double GeneratorYield(GeneratorOption o, Fact<double> componentSteel)
    {
        var steel = 0.0;
        foreach (var c in o.Costs)
        {
            if (c.Resource == SiteSteelUnit)
            {
                steel += c.Count;
            }
            else if (c.Resource == ComponentResource)
            {
                if (!componentSteel.TryGetValue(out var rate))
                {
                    return 0;
                }
                steel += c.Count * rate;
            }
            else
            {
                return 0;
            }
        }
        if (!(steel > 0))
        {
            return 0;
        }
        return o.NominalW * o.Profile.Daily / steel;
    }

    private readonly record struct RankedGenerator(string Definition, int Tier, int Order, double Yield);

    /// <summary>
    /// Orders the known-available options: a fuel-free generator first once a battery can bank its surplus,
    /// then a generator that runs around the clock on a fuel the colony can grow (or none), a stocked finite
    /// fuel next, fuel-short ones after, and a renewable that cannot serve the deficit (no battery, or a
    /// night-only shortfall) last, still chosen when nothing else is available. Within a tier the generator
    /// delivering the most watts per steel of build cost (components priced at their recipe's steel) goes
    /// first; list order breaks ties.
    /// </summary>
    public static IReadOnlyList<string> RankGenerators(IReadOnlyList<GeneratorOption> options, GeneratorRanking ranking)
    {
        var rows = new List<RankedGenerator>();
        ranking.BatteryAvailable.TryGetValue(out var battery);
        for (var i = 0; i < options.Count; i++)
        {
            var o = options[i];
            if (!o.Available.TryGetValue(out var available) || !available)
            {
                continue;
            }
            int tier;
            var profile = o.Profile;
            if (profile.Night < 1 && (!battery || (ranking.NightOnly && profile.Night == 0)))
            {
                tier = 4;
            }
            else if (profile.Night < 1)
            {
                tier = 0;
            }
            else if (o.Fuel.IsEmpty)
            {
                tier = 1;
            }
            else
            {
                tier = 1;
                if (o.FuelStock.TryGetValue(out var stock) && stock < o.MinimumFuel)
                {
                    tier = 3;
                }
                else if (!o.FuelRenewable)
                {
                    tier = 2;
                }
            }
            rows.Add(new RankedGenerator(o.Definition, tier, i, GeneratorYield(o, ranking.ComponentSteel)));
        }
        return [.. rows
            .OrderBy(r => r.Tier)
            .ThenByDescending(r => r.Yield)
            .ThenBy(r => r.Order)
            .Select(r => r.Definition)];
    }

    /// <summary>
    /// Ports the network-local capacity and bounded route choices. Proposed geometry still requires native
    /// placement previews and shared admission. Output and PowerOn, not installed capacity or a receipt,
    /// establish recovery. Producers that are out of fuel or broken down hold the proposal: refueling and
    /// repair are ordinary pawn work owned by other families. A powered network whose stored reserve runway
    /// falls under ReserveMinDays is a deficit too, sized by its 24 h budget (ComputePowerBudget): a
    /// generation shortfall adds a generator, a storage shortfall alone adds a battery, and a network the
    /// budget already covers waits for its bank to charge. Throws DomainException on invalid input.
    /// </summary>
    public static PowerProposal SelectPowerMethod(Fact<PowerTopology> fact, Bounds bounds, IReadOnlyList<SiteCell> cells, IReadOnlyList<Cell> protectedCells, PowerPlanning planning)
    {
        if (!fact.TryGetValue(out var v))
        {
            return new PowerProposal { Method = PowerMethod.Unknown };
        }
        if (!FoodNumber(planning.ReserveMinDays) || !FoodNumber(planning.StorageMargin) || !FoodNumber(planning.PendingDemandW)
            || planning.StorageMargin > 10 || planning.PendingDemandW > 1e12)
        {
            throw new DomainException("invalid power planning");
        }
        if (!(planning.Battery.CapacityWD > 0) || !(planning.Battery.Efficiency > 0 && planning.Battery.Efficiency <= 1))
        {
            throw new DomainException("invalid power planning: no catalog battery");
        }
        var networks = new Dictionary<string, PowerNetworkFact>(StringComparer.Ordinal);
        foreach (var n in v.Networks)
        {
            if (!FoodId(n.Id))
            {
                throw new DomainException("invalid power network identity");
            }
            if (networks.ContainsKey(n.Id))
            {
                throw new DomainException("duplicate power network");
            }
            foreach (var w in new[] { n.GenerationW, n.ConsumptionW, n.StoredWD, n.CapacityWD })
            {
                if (w.TryGetValue(out var x) && (double.IsNaN(x) || double.IsInfinity(x) || x < 0 || x > 1e12))
                {
                    throw new DomainException("invalid power network energy");
                }
            }
            networks[n.Id] = n;
        }
        if (bounds.Width < 1 || bounds.Height < 1 || bounds.Width > 4096 || bounds.Height > 4096 || v.Buildings.Count + v.Conduits.Count > 256)
        {
            throw new DomainException("invalid power planning bounds");
        }
        bool Inside(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < bounds.Width && c.Z < bounds.Height;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;
        foreach (var b in v.Buildings)
        {
            if (!FoodId(b.Id) || !FoodId(b.Definition) || seen.Contains(b.Id) || !Inside(b.Cell) || b.Occupied.Count < 1)
            {
                throw new DomainException("invalid power building geometry");
            }
            seen.Add(b.Id);
            var footprint = new HashSet<Cell>();
            foreach (var c in b.Occupied)
            {
                if (!Inside(c) || !footprint.Add(c))
                {
                    throw new DomainException("invalid power footprint");
                }
            }
            if (!footprint.Contains(b.Cell))
            {
                throw new DomainException("power anchor outside footprint");
            }
            foreach (var watts in new[] { b.BaseW, b.OutputW })
            {
                var k = watts.TryGetValue(out var w);
                if (k && (double.IsNaN(w) || double.IsInfinity(w) || Math.Abs(w) > 1e12))
                {
                    throw new DomainException("invalid power wattage");
                }
                complete = complete && k;
            }
            foreach (var amount in new[] { b.Fuel, b.TargetFuel, b.Stored, b.Capacity })
            {
                if (amount.TryGetValue(out var x) && (double.IsNaN(x) || double.IsInfinity(x) || x < 0 || x > 1e12))
                {
                    throw new DomainException("invalid power service amount");
                }
            }
            var ck = b.Connected.TryGetValue(out var connected);
            var nk = b.Network.TryGetValue(out var net);
            if (nk && (!FoodId(net!) || (ck && !connected)))
            {
                throw new DomainException("invalid power network");
            }
            var pk = b.Powered.TryGetValue(out _);
            var fk = b.Forbidden.TryGetValue(out _);
            var sk = b.SwitchedOn.TryGetValue(out _);
            complete = complete && ck && (!connected || nk) && pk && fk && sk;
        }
        var existing = new HashSet<Cell>();
        foreach (var c in v.Conduits)
        {
            if (!Inside(c) || !existing.Add(c))
            {
                throw new DomainException("invalid conduit census");
            }
        }
        var unsafeSeen = new HashSet<Cell>();
        foreach (var c in v.UnsafeConduits)
        {
            if (!existing.Contains(c) || !unsafeSeen.Add(c))
            {
                throw new DomainException("invalid unsafe conduit census");
            }
        }
        var blocked = new HashSet<Cell>();
        foreach (var c in protectedCells)
        {
            if (!Inside(c))
            {
                throw new DomainException("invalid protected power cell");
            }
            blocked.Add(c);
        }
        var geyserSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in v.Geysers)
        {
            if (!FoodId(g.Id) || !geyserSeen.Add(g.Id) || !Inside(g.Cell) || g.Cells.Count < 1)
            {
                throw new DomainException("invalid steam geyser census");
            }
            var anchored = false;
            foreach (var c in g.Cells)
            {
                if (!Inside(c))
                {
                    throw new DomainException("invalid steam geyser footprint");
                }
                anchored = anchored || c == g.Cell;
            }
            if (!anchored)
            {
                throw new DomainException("steam geyser anchor outside footprint");
            }
        }
        var route = new HashSet<Cell>();
        var seenCells = new HashSet<Cell>();
        foreach (var c in cells)
        {
            if (!Inside(c.Cell) || !seenCells.Add(c.Cell))
            {
                throw new DomainException("invalid power route census");
            }
            if (c.SupportsLight(planning.Light) && !blocked.Contains(c.Cell))
            {
                route.Add(c.Cell);
            }
        }
        var known = v.Blackout.TryGetValue(out var blackout);
        v.Eclipse.TryGetValue(out var eclipse);
        if (!known || !complete)
        {
            return new PowerProposal { Method = PowerMethod.Unknown };
        }
        if (blackout)
        {
            return new PowerProposal { Method = PowerMethod.WaitBlackout };
        }
        // Protect equipment before energizing or expanding the network. The native census discovers
        // vulnerable definitions; weather need not already be wet.
        foreach (var b in v.Buildings)
        {
            if (!Positive(b.RainVulnerable) || Positive(b.Roofed))
            {
                continue;
            }
            if (!b.Forbidden.TryGetValue(out var forbidden) || forbidden)
            {
                return new PowerProposal { Method = PowerMethod.WaitPlayer };
            }
            if (!b.Roofed.TryGetValue(out _))
            {
                return new PowerProposal { Method = PowerMethod.Unknown };
            }
            var (room, ok) = PowerShelter(b, cells, blocked, bounds, planning.Light);
            if (!ok)
            {
                return new PowerProposal { Method = PowerMethod.RouteBlocked };
            }
            return new PowerProposal
            {
                Method = PowerMethod.Shelter,
                Target = b.Id,
                Center = b.Cell,
                Room = room,
                Key = PowerMethodKey("shelter", b.Id + GoFormat(room)),
            };
        }
        if (v.UnsafeConduits.Count > 0)
        {
            // An unpowered, unconnected consumer joins a live network first when every ordinary conduit its
            // route crosses is already contiguous with the live producer: the new cells are HiddenConduit,
            // so connecting adds no zzztt exposure and the consumer does not wait out the upgrade plans.
            var live = ConnectLiveBeforeUpgrade(v, existing, unsafeSeen, route, planning.Net);
            if (live is not null)
            {
                return live;
            }
            var upgrade = v.UnsafeConduits.ToList();
            upgrade.Sort(LessComparer<Cell>(CellLess));
            return new PowerProposal { Method = PowerMethod.Connect, Cells = upgrade, Key = PowerMethodKey("upgrade", GoFormat(upgrade)) };
        }
        var producers = new List<PowerSite>();
        foreach (var b in v.Buildings)
        {
            b.BaseW.TryGetValue(out var w);
            if (w > 0)
            {
                producers.Add(b);
            }
        }
        var result = new PowerProposal { Method = PowerMethod.NoMethod };
        foreach (var target in v.Buildings)
        {
            target.BaseW.TryGetValue(out var baseW);
            if (baseW >= 0)
            {
                continue;
            }
            target.Forbidden.TryGetValue(out var targetForbidden);
            target.SwitchedOn.TryGetValue(out var targetSwitched);
            if (targetForbidden || !targetSwitched)
            {
                result = result with { Method = PowerMethod.WaitPlayer };
                continue;
            }
            target.Connected.TryGetValue(out var connected);
            target.Powered.TryGetValue(out var powered);
            target.Network.TryGetValue(out var network);
            bool Same(PowerSite b)
            {
                b.Network.TryGetValue(out var n);
                b.Connected.TryGetValue(out var c);
                return connected && c && n == network;
            }
            double demand = 0, capacity = 0, output = 0;
            var connectedProducers = 0;
            bool disabledProducer = false, unfueledProducer = false, brokenProducer = false;
            var budgetProducers = new List<PowerProducer>();
            double budgetCapacity = 0;
            foreach (var b in v.Buildings)
            {
                if (!Same(b))
                {
                    continue;
                }
                b.BaseW.TryGetValue(out var w);
                b.OutputW.TryGetValue(out var actual);
                output += actual;
                if (b.Capacity.TryGetValue(out var storage))
                {
                    budgetCapacity += storage;
                }
                if (w < 0)
                {
                    demand -= w;
                }
                if (w > 0)
                {
                    connectedProducers++;
                    capacity += w;
                    if (!planning.Sources.TryGetValue(b.Definition, out var profile))
                    {
                        throw new DomainException($"power producer {b.Definition} has no catalog power source profile");
                    }
                    budgetProducers.Add(new PowerProducer { Definition = b.Definition, NominalW = w, Profile = profile });
                    b.Forbidden.TryGetValue(out var f);
                    b.SwitchedOn.TryGetValue(out var s);
                    disabledProducer = disabledProducer || f || !s;
                    b.OutOfFuel.TryGetValue(out var empty);
                    b.BrokenDown.TryGetValue(out var broken);
                    unfueledProducer = unfueledProducer || empty;
                    brokenProducer = brokenProducer || broken;
                }
            }
            var budget = new PowerBudgetInput
            {
                Eclipse = eclipse,
                StorageMargin = planning.StorageMargin,
                ChargeEfficiency = planning.Battery.Efficiency,
                CapacityWD = budgetCapacity,
                Producers = budgetProducers,
                DemandW = demand + planning.PendingDemandW,
            };
            // Reserve runway only matters while the network is powered: a draining battery keeps consumers on
            // until it does not, and a network whose producers cannot carry the coming night (solar by day,
            // no bank) is short of reserve before the sun goes down.
            var reserveShort = false;
            if (connected && powered)
            {
                if (network is not null && networks.TryGetValue(network, out var net))
                {
                    if (net.ReserveDays().TryGetValue(out var days) && days < planning.ReserveMinDays)
                    {
                        reserveShort = true;
                    }
                    if (net.StoredWD.TryGetValue(out var stored))
                    {
                        var night = ComputePowerBudget(budget).NightDeficitWD;
                        if (night > 0 && stored < night)
                        {
                            reserveShort = true;
                        }
                    }
                }
            }
            if (connected && powered && output >= 0 && !reserveShort)
            {
                continue;
            }
            var p = new PowerProposal { Target = target.Id, Center = target.Cell };
            if (disabledProducer)
            {
                return p with { Method = PowerMethod.WaitPlayer };
            }
            if (connectedProducers > 0 && capacity >= demand)
            {
                if (brokenProducer)
                {
                    p = p with { Method = PowerMethod.WaitRepair };
                }
                else if (unfueledProducer)
                {
                    p = p with { Method = PowerMethod.WaitFuel };
                }
                else if (!powered || reserveShort)
                {
                    // Installed capacity covers demand on paper but the network is draining its reserve or has
                    // already lost the consumer (night-time solar, part-time generation): size it by the
                    // budget of a day. A network the budget covers waits for its bank to charge, or for
                    // native output to reach a consumer still off.
                    p = p with { Budget = ComputePowerBudget(budget) };
                    if (p.Budget.GenerationShortfallW > 0)
                    {
                        return ProposeGenerator(p, target, producers, planning, v.Geysers, route);
                    }
                    if (p.Budget.StorageShortfallWD > 0)
                    {
                        return Store(p, target, producers, planning, v.Geysers, route);
                    }
                    p = p with { Method = powered ? PowerMethod.WaitCharge : PowerMethod.WaitOutput };
                }
                else
                {
                    p = p with { Method = PowerMethod.WaitOutput };
                }
                return p;
            }
            if (producers.Count > 0 && connectedProducers == 0)
            {
                var destinations = new HashSet<Cell>();
                foreach (var b in producers)
                {
                    b.Forbidden.TryGetValue(out var f);
                    b.SwitchedOn.TryGetValue(out var s);
                    if (f || !s)
                    {
                        continue;
                    }
                    destinations.Add(b.Cell);
                    foreach (var c in b.Occupied)
                    {
                        existing.Add(c);
                    }
                }
                if (destinations.Count == 0)
                {
                    return p with { Method = PowerMethod.WaitPlayer };
                }
                var path = PlannedRoute(planning.Net, target.Cell, destinations, route);
                if (path is null || path.Count == 0)
                {
                    return p with { Method = PowerMethod.RouteBlocked };
                }
                var connect = path.Where(c => !existing.Contains(c)).ToList();
                if (connect.Count == 0)
                {
                    return p with { Method = PowerMethod.WaitOutput };
                }
                return p with { Method = PowerMethod.Connect, Cells = connect, Key = PowerMethodKey("connect", GoFormat(connect)) };
            }
            p = p with { Budget = ComputePowerBudget(budget) };
            return ProposeGenerator(p, target, producers, planning, v.Geysers, route);
        }
        return result;
    }

    /// <summary>
    /// The connect proposal for the first switched-on, unforbidden, unconnected consumer whose route reaches a
    /// producer already outputting power, provided no ordinary conduit on the route lies beyond the first new
    /// cell (such a conduit would be newly energized). Null when there is none.
    /// </summary>
    private static PowerProposal? ConnectLiveBeforeUpgrade(PowerTopology v, HashSet<Cell> existing, HashSet<Cell> unsafeCells, HashSet<Cell> route, PlannedNet net)
    {
        var occupied = new HashSet<Cell>();
        var destinations = new HashSet<Cell>();
        foreach (var b in v.Buildings)
        {
            b.BaseW.TryGetValue(out var w);
            b.OutputW.TryGetValue(out var output);
            b.Powered.TryGetValue(out var powered);
            b.Forbidden.TryGetValue(out var f);
            b.SwitchedOn.TryGetValue(out var s);
            if (w <= 0 || output <= 0 || !powered || f || !s)
            {
                continue;
            }
            destinations.Add(b.Cell);
            foreach (var c in b.Occupied)
            {
                occupied.Add(c);
            }
        }
        if (destinations.Count == 0)
        {
            return null;
        }
        foreach (var target in v.Buildings)
        {
            target.BaseW.TryGetValue(out var baseW);
            var ck = target.Connected.TryGetValue(out var connected);
            target.Forbidden.TryGetValue(out var f);
            target.SwitchedOn.TryGetValue(out var s);
            if (baseW >= 0 || !ck || connected || f || !s)
            {
                continue;
            }
            var path = PlannedRoute(net, target.Cell, destinations, route);
            var cells = new List<Cell>();
            var safe = true;
            foreach (var c in path ?? [])
            {
                if (existing.Contains(c) || occupied.Contains(c))
                {
                    if (unsafeCells.Contains(c) && cells.Count > 0)
                    {
                        safe = false;
                        break;
                    }
                    continue;
                }
                cells.Add(c);
            }
            if (!safe || cells.Count == 0)
            {
                continue;
            }
            return new PowerProposal
            {
                Method = PowerMethod.Connect,
                Target = target.Id,
                Center = target.Cell,
                Cells = cells,
                Key = PowerMethodKey("connect", GoFormat(cells)),
            };
        }
        return null;
    }

    /// <summary>
    /// A narrow, supported enclosure leaves an aisle around the equipment and a doorway. Occupied, zoned,
    /// protected or unobserved perimeter cells fail closed.
    /// </summary>
    private static (Rectangle Room, bool Ok) PowerShelter(PowerSite target, IReadOnlyList<SiteCell> cells, HashSet<Cell> protectedCells, Bounds bounds, LightTerrains light)
    {
        int minX = target.Cell.X, maxX = target.Cell.X, minZ = target.Cell.Z, maxZ = target.Cell.Z;
        var occupied = new HashSet<Cell>();
        foreach (var c in target.Occupied)
        {
            minX = Math.Min(minX, c.X);
            maxX = Math.Max(maxX, c.X);
            minZ = Math.Min(minZ, c.Z);
            maxZ = Math.Max(maxZ, c.Z);
            occupied.Add(c);
        }
        var r = new Rectangle(minX - 2, minZ - 2, maxX - minX + 5, maxZ - minZ + 5);
        if (r.X < 0 || r.Z < 0 || r.X + r.Width > bounds.Width || r.Z + r.Height > bounds.Height || r.Width > 10 || r.Height > 10)
        {
            return (default, false);
        }
        var observed = new Dictionary<Cell, SiteCell>();
        foreach (var c in cells)
        {
            observed[c.Cell] = c;
        }
        foreach (var p in RectCells(r))
        {
            if (protectedCells.Contains(p))
            {
                return (default, false);
            }
            if (occupied.Contains(p))
            {
                continue;
            }
            if (!observed.TryGetValue(p, out var c) || !Positive(c.Walkable) || !c.SupportsLight(light) || c.Occupied()
                || !Positive(Measured(c.Zone, v => !v)))
            {
                return (default, false);
            }
        }
        return (r, true);
    }

    /// <summary>
    /// Proposes one battery for target's network when the budget is short of storage but not generation. A
    /// battery is never the answer to a generation shortfall; without a compilable battery the generation path
    /// adds a generator instead, which the observed reserve still justifies.
    /// </summary>
    private static PowerProposal Store(PowerProposal p, PowerSite target, IReadOnlyList<PowerSite> producers, PowerPlanning planning, IReadOnlyList<PowerGeyser> geysers, HashSet<Cell> route)
    {
        if (!planning.BatteryAvailable.TryGetValue(out var available) || !available)
        {
            return ProposeGenerator(p, target, producers, planning, geysers, route);
        }
        var ids = producers.Select(b => b.Id).ToList();
        ids.Sort(StringComparer.Ordinal);
        var identity = $"{target.Id}/{string.Join("/", ids)}/{(int)(p.Budget.StorageNeededWD - p.Budget.StorageShortfallWD)}";
        return p with { Method = PowerMethod.Store, Definition = PolicyConstants.BatteryDefinition, Key = PowerMethodKey("store", identity) };
    }

    /// <summary>
    /// Proposes one more generator for the network of target. Native producer identity, rather than
    /// fluctuating output, distinguishes an additional capacity proposal from replay of the same deficit. A
    /// free steam geyser within GeothermalReachCells of the consumer over routeable cells hosts a geothermal
    /// generator ahead of every ranked option: it runs around the clock on no fuel.
    /// </summary>
    private static PowerProposal ProposeGenerator(PowerProposal p, PowerSite target, IReadOnlyList<PowerSite> producers, PowerPlanning planning, IReadOnlyList<PowerGeyser> geysers, HashSet<Cell> route)
    {
        var ids = producers.Select(b => b.Id).ToList();
        ids.Sort(StringComparer.Ordinal);
        p = p with { Key = PowerMethodKey("generate", target.Id + "/" + string.Join("/", ids)) };
        var geyser = ReachableGeyser(target.Cell, geysers, planning, route);
        if (geyser is not null)
        {
            return p with { Method = PowerMethod.Generate, Definition = PolicyConstants.GeothermalDefinition, Center = geyser.Cell, Cells = [.. geyser.Cells] };
        }
        // A shortfall the day already covers is night-only: no solar answers it.
        var ranking = new GeneratorRanking
        {
            BatteryAvailable = planning.BatteryAvailable,
            ComponentSteel = planning.ComponentSteel,
            NightOnly = p.Budget.GenerationShortfallW > 0 && p.Budget.DaySurplusWD > 0,
        };
        var ranked = RankGenerators(planning.Generators, ranking);
        if (ranked.Count == 0)
        {
            return p with { Method = PowerMethod.NoGenerator };
        }
        return p with { Method = PowerMethod.Generate, Definition = ranked[0], Alternatives = [.. ranked.Skip(1)] };
    }

    /// <summary>The nearest free geyser a conduit route of at most GeothermalReachCells can reach from start, when the geothermal generator is natively available. Null when none.</summary>
    private static PowerGeyser? ReachableGeyser(Cell start, IReadOnlyList<PowerGeyser> geysers, PowerPlanning planning, HashSet<Cell> route)
    {
        if (!planning.GeothermalAvailable.TryGetValue(out var available) || !available)
        {
            return null;
        }
        var destinations = new Dictionary<Cell, PowerGeyser>();
        var allowed = new HashSet<Cell>(route);
        foreach (var g in geysers)
        {
            if (g.Occupied)
            {
                continue;
            }
            foreach (var c in g.Cells)
            {
                destinations[c] = g;
                allowed.Add(c);
            }
        }
        if (destinations.Count == 0)
        {
            return null;
        }
        var targets = new HashSet<Cell>(destinations.Keys);
        // The consumer stands on its own footprint, which the census reports occupied: the search starts from
        // it regardless.
        allowed.Add(start);
        var path = PowerRoute(start, targets, allowed);
        if (path.Count == 0 || path.Count - 1 > PolicyConstants.GeothermalReachCells)
        {
            return null;
        }
        return destinations[path[0]];
    }

    private static MethodId PowerMethodKey(string prefix, string identity)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new MethodId($"{prefix}-{Convert.ToHexString(digest, 0, 12).ToLowerInvariant()}");
    }

    // Go's fmt.Sprint of a Rectangle and a []Cell, kept so the method keys stay the ones the Go planner minted.
    private static string GoFormat(Rectangle r) => $"{{{r.X} {r.Z} {r.Width} {r.Height}}}";

    private static string GoFormat(IReadOnlyList<Cell> cells) => "[" + string.Join(" ", cells.Select(c => $"{{{c.X} {c.Z}}}")) + "]";
}
