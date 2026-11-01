using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

public sealed record PowerTopology
{
    public IReadOnlyList<PowerSite> Buildings { get; init; } = [];

    public IReadOnlyList<Cell> Conduits { get; init; } = [];

    public IReadOnlyList<Cell> UnsafeConduits { get; init; } = [];

    /// <summary>A map condition that disables power (a solar flare) holds every network off.</summary>
    public bool Blackout { get; init; }

    /// <summary>The native eclipse condition: solar output is zero for the rest of it, so the day's budget carries no solar energy.</summary>
    public bool Eclipse { get; init; }

    /// <summary>The per-net energy summary: a network it does not list has no reserve runway to keep.</summary>
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
    [WireName("no_deficit")]
    NoMethod,
    [WireName("waiting_for_native_power")]
    WaitOutput,
    /// <summary>The network's capacity covers demand once the producers the power Need restores (refuel, fix) work again.</summary>
    [WireName("restoring_producers")]
    Restore,
    [WireName("solar_flare")]
    WaitBlackout,
    /// <summary>A switch on the network wants a state it does not hold yet: the flick designation waits for a pawn (Unconfirmed).</summary>
    [WireName("waiting_for_flick")]
    WaitSwitch,
    [WireName("no_observed_route")]
    RouteBlocked,
    /// <summary>Every alternative to deferring is excluded (no generator researched or buildable, no battery, no route).</summary>
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
    /// <summary>
    /// The network's switches need orders (<see cref="PowerProposal.Configuration"/>): the comparison switches low-value
    /// consumers off, a short network switches its idle producers on, and a served network switches its idle consumers back on.
    /// The power planner binds it to PowerSwitch orders (task 6.5).
    /// </summary>
    [WireName("configure_consumers")]
    Configure,
    /// <summary>The comparison's configuration holds: the consumers it switches off are off, and the rest are powered.</summary>
    [WireName("configuration_holds")]
    Configured,
    /// <summary>An unpowered thermal provision replaces a powered temperature control: the power planner builds it (task 6.5).</summary>
    [WireName("passive_thermal")]
    Passive,
    /// <summary>
    /// The interruption costs less than every alternative, or its price lacks a fact (<see cref="PowerProposal.Decision"/>
    /// names it): the network stays as it is.
    /// </summary>
    [WireName("interruption_deferred")]
    Defer,
}

/// <summary>
/// One generator definition the planner may compile. Fuel names the resource a refuelable generator burns and FuelStock the
/// observed colony stock of it; a fuel-free generator leaves both empty. Availability comes from native planning definitions
/// (research and content), never from an assumption about the installed game.
/// </summary>
public sealed record GeneratorOption
{
    public string Definition { get; init; } = "";

    public bool Available { get; init; }

    public Resource Fuel { get; init; }

    public long FuelStock { get; init; }

    /// <summary>Whether the colony can grow its fuel here (a sowable crop harvests it); false is a finite stock that runs out.</summary>
    public bool FuelRenewable { get; init; }

    /// <summary>The fuel items it burns a day running (CompProperties_Refuelable.fuelConsumptionRate over the item's fuel units); 0 for a fuel-free generator.</summary>
    public double FuelPerDay { get; init; }

    /// <summary>How the generator delivers its nominal watts over a day (DefinitionCatalog.PowerSources).</summary>
    public PowerSourceProfile Profile { get; init; }

    /// <summary>The producer's catalog wattage.</summary>
    public double NominalW { get; init; }

    /// <summary>Whether the def short-circuits in rain (CompProperties_Power.shortCircuitInRain): placed unroofed, it joins the rain exposure.</summary>
    public bool RainVulnerable { get; init; }
}

/// <summary>
/// The catalog's description of one generator definition: nominal watts, the cheapest stuff and, for a refuelable one, the accepted
/// fuels, the tank a full load takes (a fuel whose stock fills it is burnt first) and the items it burns a day.
/// </summary>
public sealed record GeneratorRow
{
    public double NominalW { get; init; }

    public required string Stuff { get; init; }

    public IReadOnlyList<Resource> Fuels { get; init; } = [];

    public long FuelCapacity { get; init; }

    public double FuelItemsPerDay { get; init; }

    /// <summary>Whether the def short-circuits in rain (CompProperties_Power.shortCircuitInRain).</summary>
    public bool RainVulnerable { get; init; }
}

/// <summary>
/// The planner-side choices SelectPowerMethod needs beyond the observed topology: which generators are compilable, whether a
/// battery is (and its storage, from the catalog), what each consumer's interruption costs and what each piece costs to build
/// (<see cref="Valuation"/>), and the passive thermal provisions the thermal model shows meeting a band.
/// </summary>
public sealed record PowerPlanning
{
    public IReadOnlyList<GeneratorOption> Generators { get; init; } = [];

    public bool BatteryAvailable { get; init; }

    public PowerBattery Battery { get; init; }

    /// <summary>The delivery profile of every producer definition the catalog has (DefinitionCatalog.PowerSources); a producer on the map that it lacks is an error.</summary>
    public IReadOnlyDictionary<string, PowerSourceProfile> Sources { get; init; } = new Dictionary<string, PowerSourceProfile>();

    /// <summary>The current twelfth's daylight hours at the map's latitude (GearClimate.DaylightHoursIn): none leaves a solar generator nothing to deliver.</summary>
    public double DaylightHours { get; init; }

    /// <summary>
    /// The wattage of consumers other goals' admitted plans are about to build, counted in the network's demand (their service
    /// unassessed) so generation and storage are sized for the colony being built, not only the one standing.
    /// </summary>
    public double PendingDemandW { get; init; }

    /// <summary>
    /// The dated power changes the plan expects (ProspectiveCapacityRules.ChainPower: a selected chain's later host's draw), with
    /// the tick they are read at: a change joins the demand once the prospective comparison prepares generation for it and its
    /// start has come.
    /// </summary>
    public IReadOnlyList<ExpectedChange> Expected { get; init; } = [];

    public Tick Now { get; init; }

    /// <summary>The terrain defs that afford Light: where the power route and a shelter may run (SiteCell.SupportsLight).</summary>
    public LightTerrains Light { get; init; } = new();

    /// <summary>
    /// The conduit net the layout plan laid (PlannedNetOf): a connect proposal runs along it, skipping the
    /// built cells, and searches the ground only where the plan holds no path.
    /// </summary>
    public PlannedNet Net { get; init; } = new();

    /// <summary>
    /// The out-of-fuel or broken-down producers whose function the power Need's restoration choice restores
    /// (<see cref="PowerRestoration.Restorable"/>): only these of the lost producers count toward a network's capacity.
    /// </summary>
    public IReadOnlySet<string> Restorable { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// What each consumer's interruption costs, each piece costs to build and the passive thermal provisions the thermal model
    /// shows (PowerValuationRules.Valuation).
    /// </summary>
    public PowerValuation Valuation { get; init; } = new();

    /// <summary>
    /// The colony stock (and whether the colony can grow it) of each fuel a refuelable producer on the map burns: the one stock
    /// every producer burning it draws on together. A producer's fuel it lacks is unread, naming it (shared_exposure:fuel_unread).
    /// A generator option's own fuel joins it where this does not list the fuel.
    /// </summary>
    public IReadOnlyDictionary<Resource, FuelSupply> Fuels { get; init; } = new Dictionary<Resource, FuelSupply>();

    /// <summary>The map's rain rate now, held over the horizon: what an exposed network's rain short circuits scale with (RainShortCircuitRules).</summary>
    public PowerRain Rain { get; init; } = new PowerRain.Unread("rain rate");

    /// <summary>
    /// The things the player's preferences leave alone (Preferences.ProtectedThings): their switches are never ordered, and one
    /// switched off draws nothing the network must serve.
    /// </summary>
    public IReadOnlySet<string> Protected { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// An unpowered thermal provision for a powered temperature control: the room's steady temperature with it (or unread naming
/// the fact), the control's band and the provision's price and lead.
/// </summary>
public sealed record PassiveProvision(string Control, PassiveSteady Steady, double BandMin, double BandMax, double CostDays, double LeadDays)
{
    /// <summary>The unpowered burner's definition that would stand where the control acts.</summary>
    public string Definition { get; init; } = "";
}

public sealed record PowerProposal
{
    public bool OnGeyser { get; init; }
    public PowerMethod Method { get; init; }

    public string Definition { get; init; } = "";

    public MethodId Key { get; init; }

    public Cell Center { get; init; }

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public Rectangle Room { get; init; }

    /// <summary>
    /// The priced generators after Definition, cheapest first, for a generate method, so a definition no site accepts (a wind
    /// turbine with every catch zone obstructed) yields to the next one under the same key.
    /// </summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    /// <summary>The comparison behind a deficit's method: every alternative with its price and the pick; null for the connect, protect and hold methods.</summary>
    public UtilityDecision? Decision { get; init; }

    /// <summary>The switch orders a configure method places (and, for a holding configuration, the consumers it keeps off).</summary>
    public PowerConfiguration? Configuration { get; init; }

    /// <summary>
    /// Reports whether the proposal names its exact placement: a geothermal generator is anchored on its
    /// geyser (Center) rather than searched for near the consumer.
    /// </summary>
    public bool FixedSite() => Method == PowerMethod.Generate && OnGeyser;
}

public static partial class PowerRules
{
    /// <summary>
    /// The generator whose placement preview must report a clear catch zone.
    /// </summary>
    public const string WindTurbineDefinition = "WindTurbine";

    /// <summary>
    /// The generator a free steam geyser hosts: SelectPowerMethod prices it on the nearest free geyser a measured conduit path
    /// reaches, the path's conduits in its price and lead.
    /// </summary>
    public const string GeothermalDefinition = "GeothermalGenerator";

    /// <summary>Every definition the power family compiles: the conduit, each generator, the geothermal generator and the battery.</summary>
    public static IReadOnlyList<string> PowerFamilyDefinitions(IEnumerable<string> generators) =>
        [Wire.Name(PowerMethod.Connect), .. generators, PowerRules.GeothermalDefinition, PowerRules.BatteryDefinition];

    /// <summary>
    /// Pairs each generator definition with its catalog row (nominal watts, fuel, tank and burn rate), the observed
    /// stock of its fuel, whether renewable says the fuel can be grown here, and its delivery profile from
    /// sources; a generator with no profile or row is an error (DomainException). Of several accepted fuels it burns
    /// one that fills its tank, a growable one before a finite one, then the largest stock.
    /// </summary>
    public static IReadOnlyList<GeneratorOption> DefaultGeneratorOptions(
        Func<string, bool> available,
        Func<Resource, long> stock,
        Func<Resource, bool> renewable,
        IReadOnlyDictionary<string, PowerSourceProfile> sources,
        IReadOnlyDictionary<string, GeneratorRow> rows)
    {
        var options = new List<GeneratorOption>(rows.Count);
        foreach (var (name, row) in rows.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!sources.TryGetValue(name, out var profile))
            {
                throw new DomainException($"generator {name} has no catalog power source profile");
            }
            RequirePowerProfile(profile);
            var option = new GeneratorOption { Definition = name, Available = available(name), Profile = profile, NominalW = row.NominalW, RainVulnerable = row.RainVulnerable };
            if (row.Fuels.Count != 0)
            {
                var fuel = row.Fuels.Select(fuel => new { Fuel = fuel, Stock = stock(fuel), Renewable = renewable(fuel) })
                    .OrderByDescending(fuel => fuel.Stock >= row.FuelCapacity)
                    .ThenByDescending(fuel => fuel.Renewable).ThenByDescending(fuel => fuel.Stock)
                    .ThenBy(fuel => fuel.Fuel.Value, StringComparer.Ordinal).First();
                option = option with { Fuel = fuel.Fuel, FuelStock = fuel.Stock, FuelRenewable = fuel.Renewable, FuelPerDay = row.FuelItemsPerDay };
            }
            options.Add(option);
        }
        return options;
    }

    /// <summary>
    /// Ports the network-local power choices. Proposed geometry still requires native placement previews and shared admission.
    /// Output and PowerOn, not installed capacity or a receipt, establish recovery. Rain-exposed equipment is sheltered and
    /// unsafe conduits upgraded first. A producer that is out of fuel or broken down counts toward its network's capacity only
    /// while the power Need's restoration choice restores it (PowerPlanning.Restorable): a network such producers cover
    /// proposes Restore. A network's demand is every consumer whose switch the colony may order, on or off, plus the pending and
    /// due expected draws; a consumer the player's preferences protect counts only while it is on. Switches are the colony's to
    /// order under Auto, whoever set them: a network whose switch wants a state it does not hold waits for the flick
    /// (WaitSwitch, nothing re-ordered); a network the balance serves with every consumer on switches its idle consumers on; a
    /// short one switches its idle producers on first. A network whose 24 h energy balance (UtilityChoiceRules.UnservedWD)
    /// leaves energy unserved is short: its alternatives (configure, route, generate, store, passive) are compared against
    /// deferring by UtilityChoiceRules.Choose, each priced by the consumers' interruption loss over the horizon; a chosen
    /// configuration orders the switches it changes, or holds (Configured) once they hold. A powered network the balance covers
    /// needs nothing; an unpowered one waits for its bank or native output. Throws DomainException on invalid input.
    /// </summary>
    public static PowerProposal SelectPowerMethod(PowerTopology v, Bounds bounds, IReadOnlyList<SiteCell> cells, IReadOnlyList<Cell> protectedCells, PowerPlanning planning)
    {
        if (!FoodForecastRules.FoodNumber(planning.PendingDemandW))
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
            if (!FoodForecastRules.FoodId(n.Id))
            {
                throw new DomainException("invalid power network identity");
            }
            if (networks.ContainsKey(n.Id))
            {
                throw new DomainException("duplicate power network");
            }
            networks[n.Id] = n;
        }
        if (bounds.Width < 1 || bounds.Height < 1 || bounds.Width > 4096 || bounds.Height > 4096 || v.Buildings.Count + v.Conduits.Count > 256)
        {
            throw new DomainException("invalid power planning bounds");
        }
        bool Inside(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < bounds.Width && c.Z < bounds.Height;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in v.Buildings)
        {
            if (!FoodForecastRules.FoodId(b.Id) || !FoodForecastRules.FoodId(b.Definition) || seen.Contains(b.Id) || !Inside(b.Cell) || b.OccupiedCells.Count < 1)
            {
                throw new DomainException("invalid power building geometry");
            }
            seen.Add(b.Id);
            var footprint = new HashSet<Cell>();
            foreach (var c in b.OccupiedCells)
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
            if (!FoodForecastRules.FoodId(g.Id) || !geyserSeen.Add(g.Id) || !Inside(g.Cell) || g.Cells.Count < 1)
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
        if (v.Blackout)
        {
            return new PowerProposal { Method = PowerMethod.WaitBlackout };
        }
        // Sheltering rain-exposed equipment is one alternative of a network's comparison, priced by the short circuits it
        // prevents (RainShortCircuitRules); the zzztt incident strikes ordinary conduits, which the upgrade below replaces first.
        var vulnerable = v.Buildings.Count(b => b.RainVulnerable);
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
            upgrade.Sort(PlannedRules.LessComparer<Cell>(CombatManhunterRules.CellLess));
            return new PowerProposal { Method = PowerMethod.Connect, Cells = upgrade, Key = PowerMethodKey("upgrade", GoFormat(upgrade)) };
        }
        // A lost producer (empty or broken down) the restoration choice does not restore produces nothing a plan can count on.
        bool Dead(PowerSite b) => b.BaseW > 0 && b.SwitchedOn && (b.Unfueled || b.BrokenDown) && !planning.Restorable.Contains(b.Id);
        // A switch the colony may order: a flick switch on a thing the player's preferences do not protect.
        bool Switchable(PowerSite b) => b.Flickable && !planning.Protected.Contains(b.Id);
        // A route destination produces: a producer switched off is its network's switch order first, never a conduit's end.
        var producers = new List<PowerSite>();
        foreach (var b in v.Buildings)
        {
            var w = b.BaseW;
            if (w > 0 && !Dead(b) && b.SwitchWanted)
            {
                producers.Add(b);
            }
        }
        var openSky = cells.Any(c => !c.Roofed && route.Contains(c.Cell));
        var result = new PowerProposal { Method = PowerMethod.NoMethod };
        foreach (var target in v.Buildings)
        {
            var baseW = target.BaseW;
            if (baseW >= 0)
            {
                continue;
            }
            if (!target.SwitchWanted && !Switchable(target))
            {
                // A protected consumer switched off draws nothing and is the player's to switch.
                continue;
            }
            var connected = target.Connected;
            var powered = target.Powered;
            var network = target.Network;
            // An unconnected consumer is a network of its own.
            bool Same(PowerSite b) => connected ? b.Connected && b.Network == network : b.Id == target.Id;
            double demand = 0, capacity = 0, output = 0, storage = 0;
            var connectedProducers = 0;
            bool restoringProducer = false, flicking = false;
            var live = new List<PowerProducer>();
            var tanks = new Dictionary<Resource, double>();
            var consumers = new List<PowerSite>();
            var idleProducers = new List<string>();
            foreach (var b in v.Buildings)
            {
                if (!Same(b))
                {
                    continue;
                }
                var w = b.BaseW;
                output += b.OutputW;
                storage += b.StorageWD;
                flicking = flicking || b.SwitchWanted != b.SwitchedOn;
                if (w < 0 && (b.SwitchWanted || Switchable(b)))
                {
                    demand -= w;
                    consumers.Add(b);
                }
                if (w > 0 && !Dead(b) && !b.SwitchWanted)
                {
                    // A producer switched off produces nothing until it is switched on; a protected one stays the player's.
                    if (Switchable(b)) idleProducers.Add(b.Id);
                    continue;
                }
                if (w > 0 && !Dead(b))
                {
                    connectedProducers++;
                    capacity += w;
                    if (!planning.Sources.TryGetValue(b.Definition, out var profile))
                    {
                        throw new DomainException($"power producer {b.Definition} has no catalog power source profile");
                    }
                    RequirePowerProfile(profile);
                    var (burner, tank) = BurnerOf(b, planning);
                    live.Add(new PowerProducer { NominalW = w, Profile = profile, Fuel = burner.Fuel, FuelPerDay = burner.FuelPerDay });
                    if (!burner.Fuel.IsEmpty) tanks[burner.Fuel] = tanks.GetValueOrDefault(burner.Fuel) + tank;
                    restoringProducer = restoringProducer || b.Unfueled || b.BrokenDown;
                }
            }
            var name = connected ? network : target.Id;
            if (flicking)
            {
                // An ordered switch (whoever ordered it) waits for its flick: the network is re-derived once it holds.
                if (result.Method == PowerMethod.NoMethod) result = new PowerProposal { Method = PowerMethod.WaitSwitch, Center = target.Cell };
                continue;
            }
            var geyser = ReachableGeyser(target.Cell, v.Geysers, route, existing);
            var generate = planning.Generators.Select(o => GenerateAlternative(o, planning, openSky, geyser)).ToList();
            var n = UtilityNetworkOf(name, consumers, live, storage, v.Eclipse, planning, generate, Switchable) with { Fuels = FuelStocks(planning, tanks) };
            var exposed = v.Buildings.Where(b => Same(b) && b.RainVulnerable && !b.Roofed).OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
            n = n with
            {
                Exposure = RainShortCircuitRules.Exposure([.. RainMembers(exposed)],
                    vulnerable, planning.Rain, n.Consumers.Sum(c => c.DrawW)),
            };
            var unserved = UtilityChoiceRules.UnservedWD(live, 0, storage, n.Consumers.Sum(c => c.DrawW), planning.Battery.Efficiency, v.Eclipse);
            var shortDay = unserved > UnservedTolerance;
            var atRisk = n.Exposure is not UtilityExposure.None;
            var p = new PowerProposal { Center = target.Cell };
            var idle = consumers.Where(c => !c.SwitchWanted).Select(c => c.Id).ToList();
            if (!shortDay && idle.Count > 0)
            {
                // The balance serves every consumer on: the idle ones are switched back on.
                return Switches(p, name, [], idle, null);
            }
            if (connected && powered && output >= 0 && !shortDay && !atRisk)
            {
                continue;
            }
            if (shortDay && idleProducers.Count > 0)
            {
                // A short network's idle producers are switched on before anything is compared: it costs nothing.
                return Switches(p, name, [], idleProducers, null);
            }
            if (connectedProducers > 0 && capacity >= demand && restoringProducer)
            {
                return p with { Method = PowerMethod.Restore };
            }
            if (producers.Count > 0 && connectedProducers == 0)
            {
                var destinations = new HashSet<Cell>();
                foreach (var b in producers)
                {
                    destinations.Add(b.Cell);
                    foreach (var c in b.OccupiedCells)
                    {
                        existing.Add(c);
                    }
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
            if (!shortDay)
            {
                // Served all day: only its exposure is compared, and bearing it may be cheaper than sheltering.
                if (atRisk && Compare(p, target, n, generate, geyser, producers, exposed, v, planning, route, existing, cells, blocked, bounds) is
                    { Method: not (PowerMethod.Defer or PowerMethod.NoGenerator) } sheltered) return sheltered;
                if (connected && powered && output >= 0) continue;
                // The day's balance covers the network: a bank still charging, or native output not yet reaching a consumer.
                return p with { Method = powered ? PowerMethod.WaitCharge : PowerMethod.WaitOutput };
            }
            var chosen = Compare(p, target, n, generate, geyser, producers, exposed, v, planning, route, existing, cells, blocked, bounds);
            if (chosen.Decision is { } decision && UtilityChoiceRules.SwitchedOffBy(decision) is { } off)
            {
                // The configuration's orders: each consumer it switches off that still wants on, each other one switched off.
                var orderOff = consumers.Where(c => off.Contains(c.Id) && c.SwitchWanted).Select(c => c.Id).ToList();
                var orderOn = consumers.Where(c => !off.Contains(c.Id) && !c.SwitchWanted).Select(c => c.Id).ToList();
                if (orderOff.Count + orderOn.Count > 0) return Switches(chosen, name, orderOff, orderOn, decision);
                if (result.Method == PowerMethod.NoMethod)
                {
                    result = chosen with { Method = PowerMethod.Configured, Configuration = new PowerConfiguration([.. off.Order(StringComparer.Ordinal)], []) };
                }
                continue;
            }
            if (idle.Count > 0)
            {
                // Supply is still to come: an idle consumer the network serves now, most valued per watt first, is switched on.
                var served = ServedIdle(n, idle);
                if (served.Count > 0) return Switches(p, name, [], served, null);
            }
            return chosen;
        }
        return result;
    }

    /// <summary>
    /// The idle consumers the network serves now beside its consumers on, added most valued per watt first (an unassessed one
    /// last, then id) while the 24 h balance leaves nothing unserved.
    /// </summary>
    private static List<string> ServedIdle(UtilityNetwork n, IReadOnlyList<string> idle)
    {
        var off = idle.ToHashSet(StringComparer.Ordinal);
        var load = n.Consumers.Where(c => !off.Contains(c.Building)).Sum(c => c.DrawW);
        var served = new List<string>();
        foreach (var c in n.Consumers.Where(c => off.Contains(c.Building))
            .OrderByDescending(c => c.Loss is InterruptionLoss.PerDay { CapabilityDays: var days } && c.DrawW > 0 ? days / c.DrawW : -1)
            .ThenBy(c => c.Building, StringComparer.Ordinal))
        {
            if (UtilityChoiceRules.UnservedWD(n.Producers, 0, n.StorageWD, load + c.DrawW, n.ChargeEfficiency, n.Eclipse) > UnservedTolerance) continue;
            load += c.DrawW;
            served.Add(c.Building);
        }
        return served;
    }

    /// <summary>The switch orders of one network, keyed by the network and the orders so the same orders continue their Method.</summary>
    private static PowerProposal Switches(PowerProposal p, string network, IReadOnlyList<string> off, IReadOnlyList<string> on, UtilityDecision? decision)
    {
        var configuration = new PowerConfiguration([.. off.Order(StringComparer.Ordinal)], [.. on.Order(StringComparer.Ordinal)]);
        return p with
        {
            Method = PowerMethod.Configure, Configuration = configuration, Decision = decision,
            Key = PowerMethodKey("configure", network + "/off:" + string.Join("/", configuration.SwitchOff) + "/on:" + string.Join("/", configuration.SwitchOn)),
        };
    }

    // Energy below this many watt-days a day is the balance's rounding, not a shortfall.
    private const double UnservedTolerance = 1e-6;


    /// <summary>
    /// The connect proposal for the first unconnected consumer whose route reaches a producer already outputting power, provided no ordinary conduit on the route lies beyond the first new
    /// cell (such a conduit would be newly energized). Null when there is none.
    /// </summary>
    private static PowerProposal? ConnectLiveBeforeUpgrade(PowerTopology v, HashSet<Cell> existing, HashSet<Cell> unsafeCells, HashSet<Cell> route, PlannedNet net)
    {
        var occupied = new HashSet<Cell>();
        var destinations = new HashSet<Cell>();
        foreach (var b in v.Buildings)
        {
            var w = b.BaseW;
            if (w <= 0 || b.OutputW <= 0 || !b.Powered)
            {
                continue;
            }
            destinations.Add(b.Cell);
            foreach (var c in b.OccupiedCells)
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
            var baseW = target.BaseW;
            if (baseW >= 0 || target.Connected)
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
        foreach (var c in target.OccupiedCells)
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
        foreach (var p in PlannedRules.RectCells(r))
        {
            if (protectedCells.Contains(p))
            {
                return (default, false);
            }
            if (occupied.Contains(p))
            {
                continue;
            }
            if (!observed.TryGetValue(p, out var c) || !c.Walkable || !c.SupportsLight(light) || c.Occupied() || c.Zone)
            {
                return (default, false);
            }
        }
        return (r, true);
    }

    /// <summary>
    /// The network as the comparison sees it: its consumers with their valued interruption (an unvalued one unassessed naming its
    /// definition) and whether the colony may switch them, the pending consumers' draw (service unassessed), and each dated power change the prospective comparison
    /// (ProspectiveCapacityRules.Choose, the change's whole loss over the cheapest assessed generator's lead and price) prepares
    /// and whose start has come. A change whose loss is unassessed defers and adds nothing.
    /// </summary>
    private static UtilityNetwork UtilityNetworkOf(string network, IReadOnlyList<PowerSite> consumers, IReadOnlyList<PowerProducer> live, double storage, bool eclipse,
        PowerPlanning planning, IReadOnlyList<UtilityAlternative> generate, Func<PowerSite, bool> switchable)
    {
        var rows = new List<ConsumerInterruption>();
        foreach (var c in consumers)
        {
            var (service, loss) = planning.Valuation.Consumers.TryGetValue(c.Id, out var value)
                ? (value.Service, value.Loss) : (UtilityService.Unmodelled, new InterruptionLoss.Unassessed(ConsumerServiceFact + c.Definition));
            rows.Add(new(c.Id, -c.BaseW, service, loss) { Switchable = switchable(c) });
        }
        if (planning.PendingDemandW > 0)
        {
            rows.Add(new("pending", planning.PendingDemandW, UtilityService.Unmodelled, new InterruptionLoss.Unassessed("pending consumer service")) { Switchable = false });
        }
        ProvisionOption[] prepare = [.. generate.Where(a => a.Standing is UtilityStanding.Assessed)
            .Select(a => (a.Subject, Effect: ((UtilityStanding.Assessed)a.Standing).Effect))
            .Select(x => new ProvisionOption(x.Subject, false, x.Effect.LeadDays, x.Effect.CostDays))
            .OrderBy(o => o.CostDays).ThenBy(o => o.Kind, StringComparer.Ordinal).Take(1)];
        foreach (var change in planning.Expected.Where(c => c.Owner == ExpectedOwner.Power))
        {
            // The change's loss is the whole draw's: one provision serves it, so it is compared as one unit.
            if (!ProspectiveCapacityRules.Choose(change with { Units = 1 }, planning.Now, prepare).Due(planning.Now)) continue;
            var loss = ((ExpectedLoss.PerDay)change.Loss).CapabilityDays;
            rows.Add(new(change.Source, change.Units, UtilityService.Workshop, new InterruptionLoss.PerDay(loss)) { Switchable = false });
        }
        return new UtilityNetwork
        {
            Network = network, Producers = live, StorageWD = storage, Consumers = rows,
            ChargeEfficiency = planning.Battery.Efficiency, Eclipse = eclipse,
        };
    }

    /// <summary>The fact an unvalued consumer's interruption names: its definition's service.</summary>
    public const string ConsumerServiceFact = "consumer service:";

    /// <summary>The fact an unpriced piece names.</summary>
    public const string BuildPriceFact = "build price:";

    /// <summary>
    /// One generator as an alternative: excluded when unavailable (research, content or builder), a solar generator with no
    /// unroofed routeable cell or no daylight this twelfth, a geothermal generator with no free geyser a conduit path reaches,
    /// or a finite fuel the colony holds none of; unassessed while its piece is unpriced or a finite fuel's burn rate is unread.
    /// A finite fuel runs out with the colony's one stock of it, shared with every producer already burning it (UtilityChoiceRules.Choose);
    /// a geothermal generator's price and lead carry its path's conduits.
    /// </summary>
    private static UtilityAlternative GenerateAlternative(GeneratorOption o, PowerPlanning planning, bool openSky, (PowerGeyser Geyser, int Conduits)? geyser)
    {
        var def = o.Definition;
        UtilityAlternative Of(UtilityStanding standing) => new(UtilityChoiceKind.Generate, def, standing);
        if (!o.Available) return Of(new UtilityStanding.Excluded("unavailable:" + def));
        RequirePowerProfile(o.Profile);
        if (!double.IsFinite(o.NominalW) || o.NominalW <= 0) throw new RequiredFactException("generator output:" + def);
        if (o.Profile.Solar && !openSky) return Of(new UtilityStanding.Excluded("no_open_sky"));
        if (o.Profile.Solar && planning.DaylightHours <= 0) return Of(new UtilityStanding.Excluded("no_daylight"));
        if (o.Profile.Steam && geyser is null) return Of(new UtilityStanding.Excluded("no_free_geyser"));
        if (!o.Fuel.IsEmpty && !o.FuelRenewable)
        {
            if (o.FuelStock <= 0) return Of(new UtilityStanding.Excluded("no_fuel:" + o.Fuel.Value));
            if (!double.IsFinite(o.FuelPerDay) || o.FuelPerDay <= 0) return Of(new UtilityStanding.Unassessed("fuel burn rate:" + def));
        }
        if (!planning.Valuation.Pieces.TryGetValue(def, out var piece)) return Of(new UtilityStanding.Unassessed(BuildPriceFact + def));
        double cost = piece.CostDays, lead = piece.LeadDays;
        if (o.Profile.Steam && geyser is { Conduits: > 0 and var conduits })
        {
            if (!planning.Valuation.Pieces.TryGetValue(Wire.Name(PowerMethod.Connect), out var conduit))
                return Of(new UtilityStanding.Unassessed(BuildPriceFact + Wire.Name(PowerMethod.Connect)));
            cost += conduit.CostDays * conduits;
            lead += conduit.LeadDays * conduits;
        }
        return Of(new UtilityStanding.Assessed(new UtilityEffect
        {
            Producers = [new PowerProducer { NominalW = o.NominalW, Profile = o.Profile, Fuel = o.Fuel, FuelPerDay = o.FuelPerDay }], CostDays = cost, LeadDays = lead,
        }));
    }

    /// <summary>The rain-exposed members the short-circuit roll prices: each one's stored energy (a battery) and whether it can short now.</summary>
    private static IEnumerable<(bool Battery, double StoredWD, bool On)> RainMembers(IEnumerable<PowerSite> members) =>
        members.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => (b.Service.Power is RimGovernor.Brain.GameFacts.Observations.BuildingPower.Battery, b.StorageWD, b.Powered));

    /// <summary>
    /// A generator that short-circuits in rain joins the network's rain exposure as a reserve under the same sky: the exposure
    /// with it standing. A solar generator stands on an unroofed cell, so its member is exposed; any other rain-vulnerable
    /// candidate's site is chosen at placement, so its roof is unread and the alternative is unpriced naming it. A def that does
    /// not short in rain leaves the exposure as it is.
    /// </summary>
    private static UtilityAlternative WithRain(UtilityAlternative alternative, List<PowerSite> exposed, int vulnerable, double demandW, PowerPlanning planning)
    {
        if (alternative.Standing is not UtilityStanding.Assessed { Effect: var effect }) return alternative;
        var option = planning.Generators.Single(o => o.Definition == alternative.Subject);
        if (!option.RainVulnerable) return alternative;
        UtilityExposure exposure = option.Profile.Solar
            ? RainShortCircuitRules.Exposure([.. RainMembers(exposed), (false, 0, true)], vulnerable + 1, planning.Rain, demandW)
            : new UtilityExposure.Unassessed(RainSiteUnreadFact + option.Definition);
        return alternative with { Standing = new UtilityStanding.Assessed(effect with { Exposure = exposure }) };
    }

    /// <summary>The fact an unroofed-or-not candidate site names: where the generator will stand is chosen at placement.</summary>
    public const string RainSiteUnreadFact = "shared_exposure:rain_site_unread:";

    /// <summary>
    /// A refuelable producer's burner: the fuel it draws on (the accepted fuel the colony holds, else the first it accepts, as the
    /// refuel work picks it) and the items it burns a day running (the building's own difficulty-adjusted burn over its fuel
    /// units per item), with the items already in its tank. A fuel-free producer burns nothing.
    /// </summary>
    private static (PowerProducer Burner, double TankItems) BurnerOf(PowerSite b, PowerPlanning planning)
    {
        if (b.Fuel is not RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable tank || b.FuelDefinitions.Count == 0) return (new PowerProducer(), 0);
        var burn = tank.Burn;
        if (!double.IsFinite(burn.UnitsPerItem) || burn.UnitsPerItem <= 0 || !double.IsFinite(burn.PerDay) || burn.PerDay < 0 || !double.IsFinite(tank.Fuel) || tank.Fuel < 0)
            throw new DomainException($"invalid fuel burn of power producer {b.Id}");
        var fuel = b.FuelDefinitions.Select(d => new Resource(d)).FirstOrDefault(f => planning.Fuels.TryGetValue(f, out var supply) && supply.Items > 0);
        if (fuel.IsEmpty) fuel = new Resource(b.FuelDefinitions[0]);
        return (new PowerProducer { Fuel = fuel, FuelPerDay = burn.PerDay / burn.UnitsPerItem }, tank.Fuel / burn.UnitsPerItem);
    }

    /// <summary>
    /// The one stock of each fuel the network's producers and the candidate generators burn: the colony stock the plan reads
    /// (the planning's, else the generator option's own) plus the items in the network's tanks, which the producers burn first.
    /// A fuel the plan does not read is absent, so any price that needs it is unread.
    /// </summary>
    private static Dictionary<Resource, FuelSupply> FuelStocks(PowerPlanning planning, IReadOnlyDictionary<Resource, double> tanks)
    {
        var stocks = new Dictionary<Resource, FuelSupply>();
        foreach (var o in planning.Generators.Where(o => !o.Fuel.IsEmpty)) stocks[o.Fuel] = new(o.FuelStock, o.FuelRenewable);
        foreach (var (fuel, supply) in planning.Fuels) stocks[fuel] = supply;
        foreach (var (fuel, items) in tanks)
        {
            if (stocks.TryGetValue(fuel, out var supply)) stocks[fuel] = supply with { Items = supply.Items + items };
        }
        return stocks;
    }

    /// <summary>
    /// The short network's comparison (UtilityChoiceRules.Choose) over configuring its consumers, a conduit to each network with
    /// a surplus (its current surplus held constant), each generator, a battery and each passive thermal provision for one of its
    /// controls, and the method the pick binds.
    /// </summary>
    private static PowerProposal Compare(PowerProposal p, PowerSite target, UtilityNetwork n, IReadOnlyList<UtilityAlternative> generate, (PowerGeyser Geyser, int Conduits)? geyser,
        IReadOnlyList<PowerSite> producers, List<PowerSite> exposed, PowerTopology v, PowerPlanning planning, HashSet<Cell> route, HashSet<Cell> existing,
        IReadOnlyList<SiteCell> cells, HashSet<Cell> blocked, Bounds bounds)
    {
        var alternatives = new List<UtilityAlternative> { UtilityChoiceRules.Configuration(n) };
        var vulnerable = v.Buildings.Count(b => b.RainVulnerable);
        var demand = n.Consumers.Sum(c => c.DrawW);
        var routes = new Dictionary<string, List<Cell>>(StringComparer.Ordinal);
        var conduitName = Wire.Name(PowerMethod.Connect);
        foreach (var other in v.Networks.Where(net => net.Id != n.Network && net.GenerationW - net.ConsumptionW > 0).OrderBy(net => net.Id, StringComparer.Ordinal))
        {
            var destinations = v.Buildings.Where(b => b.Connected && b.Network == other.Id).SelectMany(b => b.OccupiedCells).ToHashSet();
            var path = destinations.Count == 0 ? null : PlannedRoute(planning.Net, target.Cell, destinations, route);
            if (path is not { Count: > 0 })
            {
                alternatives.Add(new(UtilityChoiceKind.Route, other.Id, new UtilityStanding.Excluded("no_route")));
                continue;
            }
            var connect = path.Where(c => !existing.Contains(c) && !destinations.Contains(c)).ToList();
            routes[other.Id] = connect;
            // The route joins the two networks: the merged network carries both's rain exposure, and the surplus is delivered by
            // the other network's producers, which burn the colony's one stock of their fuel beside this network's.
            var surplus = other.GenerationW - other.ConsumptionW;
            var joined = RainShortCircuitRules.Exposure([.. RainMembers(exposed), .. RainMembers(v.Buildings.Where(b => b.Connected && b.Network == other.Id && b.RainVulnerable && !b.Roofed))],
                vulnerable, planning.Rain, demand + other.ConsumptionW);
            var fed = producers.Where(b => b.Connected && b.Network == other.Id).Select(b => (Nominal: b.BaseW, Burner: BurnerOf(b, planning))).ToArray();
            var fedW = fed.Sum(f => f.Nominal);
            var backed = fed.Where(f => !f.Burner.Burner.Fuel.IsEmpty).GroupBy(f => f.Burner.Burner.Fuel)
                .Select(g => new FuelBackedSupply(g.Key, g.Sum(f => f.Burner.Burner.FuelPerDay), surplus * g.Sum(f => f.Nominal) / fedW, g.Sum(f => f.Burner.TankItems))).ToArray();
            alternatives.Add(new(UtilityChoiceKind.Route, other.Id, !planning.Valuation.Pieces.TryGetValue(conduitName, out var conduit)
                ? new UtilityStanding.Unassessed(BuildPriceFact + conduitName)
                : new UtilityStanding.Assessed(new UtilityEffect
                {
                    SupplyW = surplus - backed.Sum(s => s.W), FuelSupplies = backed, Exposure = joined,
                    CostDays = conduit.CostDays * connect.Count, LeadDays = conduit.LeadDays * connect.Count,
                })));
        }
        alternatives.AddRange(generate.Select(g => WithRain(g, exposed, vulnerable, demand, planning)));
        alternatives.Add(new(UtilityChoiceKind.Store, PowerRules.BatteryDefinition, !planning.BatteryAvailable
            ? new UtilityStanding.Excluded("unavailable:" + PowerRules.BatteryDefinition)
            : !planning.Valuation.Pieces.TryGetValue(PowerRules.BatteryDefinition, out var battery)
                ? new UtilityStanding.Unassessed(BuildPriceFact + PowerRules.BatteryDefinition)
                : new UtilityStanding.Assessed(new UtilityEffect { StorageWD = planning.Battery.CapacityWD, CostDays = battery.CostDays, LeadDays = battery.LeadDays })));
        var controls = n.Consumers.Select(c => c.Building).ToHashSet(StringComparer.Ordinal);
        var passives = planning.Valuation.Passive.Where(x => controls.Contains(x.Control))
            .OrderBy(x => x.Control, StringComparer.Ordinal).ThenBy(x => x.Definition, StringComparer.Ordinal).ToArray();
        alternatives.AddRange(passives.Select(UtilityChoiceRules.Passive));
        var (shelter, shelterRoom) = ShelterAlternative(exposed, cells, blocked, bounds, planning);
        alternatives.Add(shelter);
        var decision = UtilityChoiceRules.Choose(n, alternatives);
        p = p with { Decision = decision };
        var subject = decision.Chosen.Alternative.Subject;
        var ids = producers.Select(b => b.Id).Order(StringComparer.Ordinal).ToList();
        switch (decision.Kind)
        {
            case UtilityChoiceKind.Defer:
                // No supply to add at all (no generator, battery or route stands) is NoGenerator, which the research gate explains.
                return p with { Method = alternatives.Where(a => a.Kind is UtilityChoiceKind.Generate or UtilityChoiceKind.Store or UtilityChoiceKind.Route)
                    .All(a => a.Standing is UtilityStanding.Excluded) ? PowerMethod.NoGenerator : PowerMethod.Defer };
            case UtilityChoiceKind.Configure:
                // SelectPowerMethod turns the switched-off set into the switch orders the observed switches need.
                return p with { Method = PowerMethod.Configure };
            case UtilityChoiceKind.Route:
                var routed = routes[subject];
                if (routed.Count == 0) return p with { Method = PowerMethod.WaitOutput };
                return p with { Method = PowerMethod.Connect, Cells = routed, Key = PowerMethodKey("connect", GoFormat(routed)) };
            case UtilityChoiceKind.Generate:
                p = p with { Method = PowerMethod.Generate, Definition = subject, Key = PowerMethodKey("generate", target.Id + "/" + string.Join("/", ids)) };
                bool Steam(string def) => planning.Generators.Single(o => o.Definition == def).Profile.Steam;
                var steam = Steam(subject);
                // The priced generators a placement may fall back to under the same key, cheapest first: on the same geyser for a
                // geothermal pick, else every other ordinary one.
                p = p with
                {
                    Alternatives = [.. decision.Alternatives.Where(a => a.Alternative.Kind == UtilityChoiceKind.Generate && a.Price is UtilityPrice.Days && a.Alternative.Subject != subject
                            && Steam(a.Alternative.Subject) == steam)
                        .OrderBy(a => ((UtilityPrice.Days)a.Price).Value).ThenBy(a => a.Alternative.Subject, StringComparer.Ordinal).Select(a => a.Alternative.Subject)],
                };
                return steam && geyser is { Geyser: var site } ? p with { OnGeyser = true, Center = site.Cell, Cells = [.. site.Cells] } : p;
            case UtilityChoiceKind.Store:
                return p with { Method = PowerMethod.Store, Definition = PowerRules.BatteryDefinition,
                    Key = PowerMethodKey("store", $"{target.Id}/{string.Join("/", ids)}/{(int)n.StorageWD}") };
            case UtilityChoiceKind.Passive:
                var provision = passives.First(x => UtilityChoiceRules.PassiveSubject(x) == subject);
                return p with
                {
                    Method = PowerMethod.Passive, Definition = provision.Definition, Center = v.Buildings.First(b => b.Id == provision.Control).Cell,
                    Key = PowerMethodKey("passive", subject),
                };
            case UtilityChoiceKind.Shelter:
                var sheltered = exposed[0];
                return p with { Method = PowerMethod.Shelter, Center = sheltered.Cell, Room = shelterRoom, Key = PowerMethodKey("shelter", sheltered.Id + GoFormat(shelterRoom)) };
        }
        throw new DomainException("no power method binds the utility choice " + decision.Kind);
    }

    /// <summary>
    /// Sheltering the network's rain-exposed members as one alternative: a walled, roofed room around each (PowerShelter), priced
    /// at its ring's walls less a door, the door and its interior's roof cells, and clearing the network's exposure; the method
    /// binds the first member's room (the next round shelters the next). Excluded without an exposed member or for one whose
    /// room has no site; unassessed while a piece is unpriced.
    /// </summary>
    private static (UtilityAlternative Alternative, Rectangle Room) ShelterAlternative(List<PowerSite> exposed, IReadOnlyList<SiteCell> cells, HashSet<Cell> blocked, Bounds bounds, PowerPlanning planning)
    {
        UtilityAlternative Of(UtilityStanding standing) => new(UtilityChoiceKind.Shelter, exposed.Count == 0 ? "" : exposed[0].Id, standing);
        if (exposed.Count == 0) return (Of(new UtilityStanding.Excluded("no_exposure")), default);
        double cost = 0, lead = 0;
        Rectangle first = default;
        foreach (var b in exposed)
        {
            var (room, ok) = PowerShelter(b, cells, blocked, bounds, planning.Light);
            if (!ok) return (Of(new UtilityStanding.Excluded("no_shelter_site:" + b.Id)), default);
            if (first.Width == 0) first = room;
            var ring = (2 * (room.Width + room.Height)) - 4;
            foreach (var (piece, count) in new[] { (ContainmentCellRules.ShellWallDefinition, ring - 1), (ContainmentCellRules.ShellDoorDefinition, 1), (RoofPiece, (room.Width - 2) * (room.Height - 2)) })
            {
                if (!planning.Valuation.Pieces.TryGetValue(piece, out var priced)) return (Of(new UtilityStanding.Unassessed(BuildPriceFact + piece)), default);
                cost += priced.CostDays * count;
                lead += priced.LeadDays * count;
            }
        }
        return (Of(new UtilityStanding.Assessed(new UtilityEffect { CostDays = cost, LeadDays = lead, ClearsExposure = true })), first);
    }

    /// <summary>The piece a shelter's roof cells are priced by: one cell's roofing (PowerValuationRules.Valuation).</summary>
    public const string RoofPiece = "roof";

    /// <summary>
    /// The nearest free geyser a conduit route reaches from start over routeable cells, with the new conduits the route lays
    /// (its cells less the consumer, the geyser and the conduits already built). Null when none: no free geyser, or no route.
    /// </summary>
    private static (PowerGeyser Geyser, int Conduits)? ReachableGeyser(Cell start, IReadOnlyList<PowerGeyser> geysers, HashSet<Cell> route, HashSet<Cell> existing)
    {
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
        var path = ConduitRouterRules.PowerRoute(start, targets, allowed);
        if (path.Count == 0)
        {
            return null;
        }
        return (destinations[path[0]], path.Count(c => c != start && !targets.Contains(c) && !existing.Contains(c)));
    }

    private static MethodId PowerMethodKey(string prefix, string identity)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new MethodId($"{prefix}-{Convert.ToHexString(digest, 0, 12).ToLowerInvariant()}");
    }

    // The text form of a Rectangle and a []Cell, kept so the method keys stay stable.
    private static string GoFormat(Rectangle r) => $"{{{r.X} {r.Z} {r.Width} {r.Height}}}";

    private static string GoFormat(IReadOnlyList<Cell> cells) => "[" + string.Join(" ", cells.Select(c => $"{{{c.X} {c.Z}}}")) + "]";
}
